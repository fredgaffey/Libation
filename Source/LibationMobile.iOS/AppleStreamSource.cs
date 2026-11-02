using AudioPlayer;
using AudioToolbox;
using AudioUnit;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace LibationMobile.iOS;

/// <summary>
/// Decodes an m4b read through a stream rather than from a file, as for a book played without downloading, with
/// Apple's own decoder: AudioToolbox asks for bytes at the places it needs and decodes them, seeking sample accurately.
/// When the bytes have not arrived yet, a read comes back empty and says it is waiting, rather than hold the player up.
/// </summary>
public sealed class AppleStreamSource : IWaitingSource
{
	private const int CHUNK_FRAMES = 4096;

	public int SampleRate { get; }
	public int Channels { get; }
	public TimeSpan Duration { get; }

	private readonly StreamAudioFile file;
	private readonly ExtAudioFile decoder;
	private readonly long lengthFrames;
	private readonly IArrivingStream? arriving;
	private long frame;

	public bool IsWaiting { get; private set; }

	/// <param name="stream">A seekable MPEG-4 audio stream, which this source then owns.</param>
	public AppleStreamSource(Stream stream)
	{
		arriving = stream as IArrivingStream;
		file = new StreamAudioFile(stream);
		try
		{
			var error = ExtAudioFile.WrapAudioFileID(file.Handle, false, out var wrapped);
			decoder = wrapped ?? throw new IOException($"The stream could not be opened for decoding ({error}).");
			var format = decoder.FileDataFormat;
			SampleRate = (int)format.SampleRate;
			Channels = format.ChannelsPerFrame;
			// Interleaved 32-bit float, as the player wants it.
			decoder.ClientDataFormat = new AudioStreamBasicDescription
			{
				SampleRate = format.SampleRate,
				Format = AudioFormatType.LinearPCM,
				FormatFlags = AudioFormatFlags.IsFloat | AudioFormatFlags.IsPacked,
				ChannelsPerFrame = Channels,
				FramesPerPacket = 1,
				BitsPerChannel = 32,
				BytesPerFrame = 4 * Channels,
				BytesPerPacket = 4 * Channels,
			};
			lengthFrames = decoder.FileLengthFrames;
			Duration = TimeSpan.FromSeconds((double)lengthFrames / SampleRate);
		}
		catch
		{
			file.Dispose();
			throw;
		}
	}

	public unsafe int Read(Span<float> samples)
	{
		IsWaiting = arriving is not null && frame < lengthFrames && !arriving.IsReady(TimeSpan.FromSeconds((double)frame / SampleRate));
		if (IsWaiting)
			return 0;
		var framesWanted = samples.Length / Channels;
		var written = 0;
		using var buffers = new AudioBuffers(1);
		fixed (float* start = samples)
		{
			while (written < framesWanted)
			{
				var frames = Math.Min(CHUNK_FRAMES, framesWanted - written);
				buffers[0] = new AudioBuffer
				{
					NumberChannels = Channels,
					DataByteSize = frames * Channels * sizeof(float),
					Data = (nint)(start + written * Channels),
				};
				var got = (int)decoder.Read((uint)frames, buffers, out var status);
				if (status != ExtAudioFileError.OK || got == 0)
					break;
				written += got;
			}
		}
		frame += written;
		return written * Channels;
	}

	public long Seek(TimeSpan position)
	{
		frame = Math.Clamp((long)(position.TotalSeconds * SampleRate), 0, lengthFrames);
		decoder.Seek(frame);
		return frame;
	}

	public void Dispose()
	{
		decoder.Dispose();
		file.Dispose();
	}

	/// <summary>
	/// An AudioToolbox audio file whose bytes come from a stream, opened read-only through AudioFileOpenWithCallbacks.
	/// (AudioToolbox.AudioSource always passes write callbacks too, and an M4A cannot be opened for writing that way.)
	/// </summary>
	private sealed unsafe class StreamAudioFile : IDisposable
	{
		private const uint M4A_TYPE = 0x6D346166; // 'm4af'
		private const int NO_ERROR = 0;
		private const int READ_ERROR = -39; // eofErr

		private readonly Stream stream;
		private readonly byte[] buffer = new byte[64 * 1024];
		private GCHandle self;
		private nint handle;

		public nint Handle => handle;

		public StreamAudioFile(Stream stream)
		{
			this.stream = stream;
			self = GCHandle.Alloc(this);
			var status = AudioFileOpenWithCallbacks(GCHandle.ToIntPtr(self), &ReadProc, null, &SizeProc, null, M4A_TYPE, out handle);
			if (status != NO_ERROR)
			{
				self.Free();
				throw new IOException($"The stream could not be opened as audio (AudioFile error {status}).");
			}
		}

		[UnmanagedCallersOnly]
		private static int ReadProc(nint client, long position, uint requestCount, byte* data, uint* actualCount)
		{
			var file = (StreamAudioFile)GCHandle.FromIntPtr(client).Target!;
			*actualCount = 0;
			try
			{
				lock (file.stream)
				{
					file.stream.Position = position;
					var done = 0;
					while (done < requestCount)
					{
						var n = file.stream.Read(file.buffer, 0, (int)Math.Min(file.buffer.Length, requestCount - done));
						if (n <= 0)
							break;
						Marshal.Copy(file.buffer, 0, (nint)(data + done), n);
						done += n;
					}
					*actualCount = (uint)done;
				}
				return NO_ERROR;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Stream read at {position} failed: {ex.Message}");
				return READ_ERROR;
			}
		}

		[UnmanagedCallersOnly]
		private static long SizeProc(nint client) => ((StreamAudioFile)GCHandle.FromIntPtr(client).Target!).stream.Length;

		public void Dispose()
		{
			if (handle != 0)
			{
				AudioFileClose(handle);
				handle = 0;
			}
			if (self.IsAllocated)
				self.Free();
			stream.Dispose();
		}

		private const string AUDIO_TOOLBOX = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";

		[DllImport(AUDIO_TOOLBOX)]
		private static extern int AudioFileOpenWithCallbacks(nint clientData,
			delegate* unmanaged<nint, long, uint, byte*, uint*, int> read, void* write,
			delegate* unmanaged<nint, long> getSize, void* setSize, uint typeHint, out nint audioFile);

		[DllImport(AUDIO_TOOLBOX)]
		private static extern int AudioFileClose(nint audioFile);
	}
}
