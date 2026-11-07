using AudibleApi;
using AudibleApi.Authorization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LibationMobile.Services;

/// <summary>The signed-in Audible account: its tokens on disk, and an API client built from them.</summary>
public class AudibleAccount
{
	/// <summary>Audible marketplaces, by AudibleApi name, with the names people know them by.</summary>
	public static IReadOnlyList<(string Name, string DisplayName)> Regions { get; } =
	[
		("us", "United States"),
		("uk", "United Kingdom"),
		("australia", "Australia"),
		("canada", "Canada"),
		("germany", "Germany"),
		("france", "France"),
		("italy", "Italy"),
		("spain", "Spain"),
		("india", "India"),
		("japan", "Japan"),
		("brazil", "Brazil"),
	];

	private readonly string identityFile;
	private readonly MobileSettings settings;
	private readonly SemaphoreSlim apiGate = new(1, 1);
	private Api? api;
	/// <summary>The saved tokens, written back to the file whenever they change.</summary>
	private IdentityPersister? identity;

	/// <summary>
	/// The hourly access token is renewed this long before it runs out, here rather than by AudibleApi: when its own
	/// renewal fails, even for want of signal, AudibleApi unregisters the phone from the account and blanks the saved
	/// tokens, and only signing in again brings them back. Renewed ahead, a token handed out outlasts what it is for.
	/// </summary>
	private static readonly TimeSpan RenewAhead = TimeSpan.FromMinutes(15);
	private const int RENEW_ATTEMPTS = 4;

	public AudibleAccount(string identityFile, MobileSettings settings)
	{
		this.identityFile = identityFile;
		this.settings = settings;
	}

	public bool IsSignedIn => File.Exists(identityFile) && settings.RegionName is not null;

	/// <summary>The account's Audible marketplace. Only valid while signed in.</summary>
	public Locale Locale => Localization.Get(settings.RegionName);

	/// <summary>
	/// Sign in through Amazon's web page. <paramref name="login"/> shows the page and returns the URL Amazon
	/// redirects to once the user has signed in. AudibleApi then registers this app as an Android device and
	/// saves the tokens.
	/// </summary>
	public async Task SignInAsync(string regionName, ILoginChoiceEager login)
	{
		await apiGate.WaitAsync();
		try
		{
			File.Delete(identityFile);
			await EzApiCreator.GetApiAsync(login, Localization.Get(regionName), identityFile, registrationProfile: DeviceRegistrationProfile.Default);
			settings.RegionName = regionName;
			// Built again from the file, as every time, so its tokens are renewed here.
			api = null;
			identity = null;
		}
		finally
		{
			apiGate.Release();
		}
	}

	/// <summary>Sign in with an account exported from Libation desktop, instead of signing in again.</summary>
	public async Task ImportAsync(string exportJson)
	{
		await apiGate.WaitAsync();
		try
		{
			api = null;
			identity = null;
			settings.RegionName = await AudibleCliAccountImport.ImportAsync(exportJson, identityFile);
		}
		finally
		{
			apiGate.Release();
		}
	}

	/// <summary>An API client from the saved tokens, renewing the access token first if it runs out soon.</summary>
	public async Task<Api> GetApiAsync()
	{
		await apiGate.WaitAsync();
		try
		{
			if (!IsSignedIn)
				throw new InvalidOperationException("Not signed in to Audible.");
			if (identity is null)
			{
				var loaded = new IdentityPersister(identityFile);
				// Tokens missing, as after AudibleApi unregistered the phone: it would only fail asking for a password.
				if (!loaded.Identity.IsValid)
					throw new SignInLostException();
				identity = loaded;
				api = null;
			}
			if (identity.Identity.ExistingAccessToken.Expires - DateTime.UtcNow < RenewAhead)
				await RenewAsync(identity.Identity);
			// The token is current, so AudibleApi has nothing to renew while setting up.
			return api ??= new Api(await IdentityMaintainer.CreateAsync(identity.Identity));
		}
		finally
		{
			apiGate.Release();
		}
	}

	/// <summary>A new access token from the refresh token, tried again while Audible cannot be reached.</summary>
	private static async Task RenewAsync(Identity identity)
	{
		var authorize = new Authorize(identity.Locale);
		for (var attempt = 1; ; attempt++)
		{
			try
			{
				identity.Update(await authorize.RefreshAccessTokenAsync(identity.RefreshToken));
				return;
			}
			catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
			{
				if (attempt == RENEW_ATTEMPTS)
				{
					// Still usable for a while: carry on with it, and renew next time.
					if (identity.ExistingAccessToken.Expires > DateTime.UtcNow)
						return;
					throw new IOException("Audible could not be reached to renew the sign-in. Check the connection and try again.", ex);
				}
				Console.WriteLine($"Renewing the Audible sign-in failed (try {attempt}), trying again: {ex.Message}");
				await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
			}
			catch (Exception ex) when (identity.ExistingAccessToken.Expires > DateTime.UtcNow)
			{
				// Audible answered without a token. The old one still works for now; try again next time.
				Console.WriteLine($"Audible did not renew the sign-in, using the current token: {ex.Message}");
				return;
			}
			catch (Exception ex)
			{
				throw new IOException($"Audible did not renew the sign-in ({ex.Message}). If this keeps happening, sign in again.", ex);
			}
		}
	}

	/// <summary>Forget the account. Downloaded books stay on the device.</summary>
	public void SignOut()
	{
		api = null;
		identity = null;
		File.Delete(identityFile);
		settings.RegionName = null;
	}

	public static string DisplayNameOf(string? regionName)
		=> Regions.FirstOrDefault(r => r.Name == regionName).DisplayName ?? regionName ?? "";
}

/// <summary>The saved sign-in is missing its tokens and cannot be used again: only signing in again helps.</summary>
public sealed class SignInLostException() : InvalidOperationException(
	"Your Audible sign-in on this phone has stopped working. Sign out, then sign in again or import your account.");
