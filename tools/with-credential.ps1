# Runs a command with a Windows Credential Manager secret in one environment variable of that command's process.
# The secret is never printed, logged, written to disk or put on a command line.
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File with-credential.ps1 -Target <generic credential> -Variable <NAME> <command> [args...]
# The user stores the secret themselves: cmdkey /generic:<target> /user:<name> /pass   (prompts; the value isn't echoed)
param(
	[Parameter(Mandatory = $true)][string]$Target,
	[Parameter(Mandatory = $true)][string]$Variable,
	[Parameter(Mandatory = $true, ValueFromRemainingArguments = $true)][string[]]$Command
)
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class HermesCredentialReader
{
	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	struct Credential
	{
		public int Flags;
		public int Type;
		public string TargetName;
		public string Comment;
		public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
		public int CredentialBlobSize;
		public IntPtr CredentialBlob;
		public int Persist;
		public int AttributeCount;
		public IntPtr Attributes;
		public string TargetAlias;
		public string UserName;
	}

	[DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
	static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

	[DllImport("advapi32.dll")]
	static extern void CredFree(IntPtr buffer);

	public static string Read(string target)
	{
		IntPtr pointer;
		if (!CredRead(target, 1, 0, out pointer))
			throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "No generic credential named " + target + ".");
		try
		{
			var credential = (Credential)Marshal.PtrToStructure(pointer, typeof(Credential));
			return credential.CredentialBlobSize == 0 ? "" : Marshal.PtrToStringUni(credential.CredentialBlob, credential.CredentialBlobSize / 2);
		}
		finally
		{
			CredFree(pointer);
		}
	}
}
'@
$secret = [HermesCredentialReader]::Read($Target)
if ([string]::IsNullOrEmpty($secret)) { throw "The generic credential $Target is empty." }
[Environment]::SetEnvironmentVariable($Variable, $secret, 'Process')
$secret = $null
$exitCode = 1
try {
	$exe = $Command[0]
	$rest = @()
	if ($Command.Length -gt 1) { $rest = $Command[1..($Command.Length - 1)] }
	& $exe @rest
	$exitCode = $LASTEXITCODE
}
finally {
	[Environment]::SetEnvironmentVariable($Variable, $null, 'Process')
}
exit $exitCode
