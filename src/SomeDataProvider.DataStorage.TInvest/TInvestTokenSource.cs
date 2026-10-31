namespace SomeDataProvider.DataStorage.TInvest;

using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;

/// <summary>Reads exactly one configured source. The delegates allow offline tests without touching the user's vault.</summary>
public sealed class TInvestTokenSource
{
	readonly Func<string, string?> _credential;
	readonly Func<string, string?> _environment;

	public TInvestTokenSource(Func<string, string?>? credential = null, Func<string, string?>? environment = null)
	{
		_credential = credential ?? ReadCredential;
		_environment = environment ?? System.Environment.GetEnvironmentVariable;
	}

	public TInvestToken? Load(TInvestOptions options, IConfiguration configuration)
	{
		options.Validate();
		if (!options.Enabled) return null;
		var value = options.TokenSource switch
		{
			"CredentialManager" => _credential(options.EffectiveCredentialTarget),
			"Environment" => _environment("SOMEDATAPROVIDER_TINVEST_TOKEN"),
			"File" => configuration["StoresOptions:TInvest:Token"],
			_ => null,
		};
		if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("T-Invest token is missing from the selected source.");
		return new TInvestToken(value);
	}

	static string? ReadCredential(string target)
	{
		if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("T-Invest CredentialManager requires Windows.");
		if (!NativeMethods.CredRead(target, 1, 0, out var pointer)) return null;
		try
		{
			var credential = Marshal.PtrToStructure<Credential>(pointer);
			return Marshal.PtrToStringUni(credential.Blob, checked((int)credential.BlobSize / 2));
		}
		finally { NativeMethods.CredFree(pointer); }
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	struct Credential
	{
		public uint Flags;
		public uint Type;
		public IntPtr Target;
		public IntPtr Comment;
		public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
		public uint BlobSize;
		public IntPtr Blob;
		public uint Persist;
		public uint AttributeCount;
		public IntPtr Attributes;
		public IntPtr Alias;
		public IntPtr User;
	}

	static class NativeMethods
	{
		[DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

		[DllImport("advapi32.dll")]
		internal static extern void CredFree(IntPtr credential);
	}
}
