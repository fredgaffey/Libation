import AVFoundation
import Foundation
import Speech

// Speech to text on the phone, for the .NET app: Apple's SpeechAnalyzer (iOS 26), which runs on the device, is built
// for long recordings and needs no permission to read a file. A C interface, since .NET cannot call Swift.

public typealias TranscriberLine = @convention(c) (UnsafeMutableRawPointer?, Double, Double, UnsafePointer<CChar>?) -> Void
public typealias TranscriberProgress = @convention(c) (UnsafeMutableRawPointer?, Double) -> Void
public typealias TranscriberDone = @convention(c) (UnsafeMutableRawPointer?, Int32, UnsafePointer<CChar>?) -> Void

/// Whether this device can transcribe: iOS 26 or later, and hardware the model runs on.
@_cdecl("libation_transcriber_available")
public func libation_transcriber_available() -> Int32 {
	if #available(iOS 26, macOS 26, *) {
		return SpeechTranscriber.isAvailable ? 1 : 0
	}
	return 0
}

/// Whether the model for `locale` (such as "en-US") is on the device: 1 installed, 0 to be downloaded, -1 the
/// language is not supported.
@_cdecl("libation_transcriber_installed")
public func libation_transcriber_installed(_ locale: UnsafePointer<CChar>) -> Int32 {
	guard #available(iOS 26, macOS 26, *) else { return -1 }
	let wanted = Locale(identifier: String(cString: locale))
	let result = Box<Int32>(-1)
	let done = DispatchSemaphore(value: 0)
	Task {
		if let found = await SpeechTranscriber.supportedLocale(equivalentTo: wanted) {
			let installed = await SpeechTranscriber.installedLocales
			result.value = installed.contains { $0.identifier(.bcp47) == found.identifier(.bcp47) } ? 1 : 0
		}
		done.signal()
	}
	done.wait()
	return result.value
}

/// Download the model for `locale`, reporting 0 to 1, then call `done` with 1, or 0 and why not.
@_cdecl("libation_transcriber_install")
public func libation_transcriber_install(_ locale: UnsafePointer<CChar>, _ context: UnsafeMutableRawPointer?,
	_ progress: TranscriberProgress, _ done: TranscriberDone) {
	guard #available(iOS 26, macOS 26, *) else {
		failed(done, context, "Transcribing needs iOS 26.")
		return
	}
	let wanted = Locale(identifier: String(cString: locale))
	Task {
		do {
			guard let found = await SpeechTranscriber.supportedLocale(equivalentTo: wanted) else {
				failed(done, context, "This phone cannot transcribe \(wanted.identifier).")
				return
			}
			let transcriber = SpeechTranscriber(locale: found, preset: .transcription)
			if let request = try await AssetInventory.assetInstallationRequest(supporting: [transcriber]) {
				let watch = request.progress.observe(\.fractionCompleted) { p, _ in progress(context, p.fractionCompleted) }
				try await request.downloadAndInstall()
				watch.invalidate()
			}
			done(context, 1, nil)
		} catch {
			failed(done, context, error.localizedDescription)
		}
	}
}

/// Transcribe the audio file at `path` from `from` seconds to its end. Each finished stretch of speech goes to `line`
/// (start and end in seconds of the file, and the text), how far it has got to `progress`, and the end to `done`.
/// Returns a handle for `libation_transcriber_cancel` and `libation_transcriber_release`.
@_cdecl("libation_transcriber_start")
public func libation_transcriber_start(_ path: UnsafePointer<CChar>, _ from: Double, _ locale: UnsafePointer<CChar>,
	_ context: UnsafeMutableRawPointer?, _ line: TranscriberLine, _ progress: TranscriberProgress,
	_ done: TranscriberDone) -> UnsafeMutableRawPointer? {
	guard #available(iOS 26, macOS 26, *) else {
		failed(done, context, "Transcribing needs iOS 26.")
		return nil
	}
	let job = TranscriptionJob()
	let url = URL(fileURLWithPath: String(cString: path))
	let wanted = Locale(identifier: String(cString: locale))
	job.task = Task {
		do {
			try await job.run(url: url, from: from, locale: wanted, context: context, line: line, progress: progress)
			done(context, 1, nil)
		} catch is CancellationError {
			failed(done, context, "Stopped.")
		} catch {
			failed(done, context, error.localizedDescription)
		}
	}
	return Unmanaged.passRetained(job).toOpaque()
}

@_cdecl("libation_transcriber_cancel")
public func libation_transcriber_cancel(_ handle: UnsafeMutableRawPointer) {
	Unmanaged<TranscriptionJob>.fromOpaque(handle).takeUnretainedValue().task?.cancel()
}

@_cdecl("libation_transcriber_release")
public func libation_transcriber_release(_ handle: UnsafeMutableRawPointer) {
	Unmanaged<TranscriptionJob>.fromOpaque(handle).release()
}

private func failed(_ done: TranscriberDone, _ context: UnsafeMutableRawPointer?, _ message: String) {
	message.withCString { done(context, 0, $0) }
}

private final class Box<T>: @unchecked Sendable {
	var value: T
	init(_ value: T) { self.value = value }
}

private final class TranscriptionJob: @unchecked Sendable {
	/// Audio read ahead of what has been transcribed is held in memory; this much, in seconds, at most.
	private static let readAhead = 120.0
	/// Seconds read from the file at a time.
	private static let chunk = 4.0

	var task: Task<Void, Never>?
	private let lock = NSLock()
	private var heard = 0.0

	@available(iOS 26, macOS 26, *)
	func run(url: URL, from: Double, locale: Locale, context: UnsafeMutableRawPointer?, line: TranscriberLine,
		progress: TranscriberProgress) async throws {
		guard let found = await SpeechTranscriber.supportedLocale(equivalentTo: locale) else {
			throw NSError(domain: "Transcriber", code: 1, userInfo: [NSLocalizedDescriptionKey: "This phone cannot transcribe \(locale.identifier)."])
		}
		let transcriber = SpeechTranscriber(locale: found, preset: .transcription)
		let file = try AVAudioFile(forReading: url)
		let source = file.processingFormat
		guard let format = await SpeechAnalyzer.bestAvailableAudioFormat(compatibleWith: [transcriber], considering: source),
			let converter = AVAudioConverter(from: source, to: format) else {
			throw NSError(domain: "Transcriber", code: 2, userInfo: [NSLocalizedDescriptionKey: "The book's audio cannot be read for transcribing."])
		}
		let analyzer = SpeechAnalyzer(modules: [transcriber])
		try await analyzer.prepareToAnalyze(in: format)
		let (inputs, feed) = AsyncStream<AnalyzerInput>.makeStream()

		// Times from the analyser count from the first sound given it, which is `from` seconds into the file.
		heard = from
		let results = Task {
			for try await result in transcriber.results {
				let start = from + result.range.start.seconds
				let end = start + result.range.duration.seconds
				let text = String(result.text.characters).trimmingCharacters(in: .whitespacesAndNewlines)
				if !text.isEmpty {
					text.withCString { line(context, start, end, $0) }
				}
				lock.withLock { heard = max(heard, end) }
				progress(context, end)
			}
		}

		try await analyzer.start(inputSequence: inputs)
		file.framePosition = AVAudioFramePosition(from * source.sampleRate)
		let frames = AVAudioFrameCount(Self.chunk * source.sampleRate)
		var read = from
		do {
			while true {
				try Task.checkCancellation()
				// Wait for the analyser to catch up, but not for ever: a long stretch without speech gives no results.
				var waited = 0
				while read - lock.withLock({ heard }) > Self.readAhead && waited < 200 {
					try await Task.sleep(nanoseconds: 50_000_000)
					waited += 1
				}
				let left = file.length - file.framePosition
				if left <= 0 { break }
				guard let buffer = AVAudioPCMBuffer(pcmFormat: source, frameCapacity: frames) else { break }
				try file.read(into: buffer, frameCount: AVAudioFrameCount(min(Int64(frames), left)))
				if buffer.frameLength == 0 { break }
				read += Double(buffer.frameLength) / source.sampleRate
				feed.yield(AnalyzerInput(buffer: try convert(buffer, with: converter, to: format)))
			}
			feed.finish()
			try await analyzer.finalizeAndFinishThroughEndOfInput()
		} catch {
			feed.finish()
			await analyzer.cancelAndFinishNow()
			results.cancel()
			throw error
		}
		try await results.value
	}

	private func convert(_ buffer: AVAudioPCMBuffer, with converter: AVAudioConverter, to format: AVAudioFormat) throws -> AVAudioPCMBuffer {
		let capacity = AVAudioFrameCount(Double(buffer.frameLength) * format.sampleRate / buffer.format.sampleRate) + 1024
		guard let out = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: capacity) else {
			throw NSError(domain: "Transcriber", code: 3, userInfo: [NSLocalizedDescriptionKey: "Out of memory."])
		}
		var given = false
		var error: NSError?
		let status = converter.convert(to: out, error: &error) { _, status in
			if given {
				status.pointee = .noDataNow
				return nil
			}
			given = true
			status.pointee = .haveData
			return buffer
		}
		if let error { throw error }
		if status == .error {
			throw NSError(domain: "Transcriber", code: 4, userInfo: [NSLocalizedDescriptionKey: "The book's audio could not be converted for transcribing."])
		}
		return out
	}
}
