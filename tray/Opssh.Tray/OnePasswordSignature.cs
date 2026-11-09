using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;

namespace Opssh.Tray;

internal static class OnePasswordSignature
{
    // Published by 1Password for its Windows Trusted Signing identity.
    private const string PublisherUsage = "1.3.6.1.4.1.311.97.661420558.769123285.207353056.500447802";
    // Public trust root used by Microsoft's Artifact Signing subscriber identities.
    private const string PublisherRoot = "5367F20C7ADE0E2BCA790915056D086B720C33C1FA2A2661ACF787E3292E1270";
    private static readonly Guid VerifyAuthenticode = new("00aac56b-cd44-11d0-8cc2-00c04fc295ee");

    internal static bool Verify(string path, SafeFileHandle file)
    {
        var name = Marshal.StringToCoTaskMemUni(path);
        try
        {
            var subject = new TrustFile
            {
                Size = (uint)Marshal.SizeOf<TrustFile>(),
                Path = name,
                File = file.DangerousGetHandle()
            };
            return Verify(ref subject);
        }
        finally
        {
            GC.KeepAlive(file);
            Marshal.FreeCoTaskMem(name);
        }
    }

    private static bool Verify(ref TrustFile file)
    {
        const uint verify = 1, close = 2;
        const uint cacheOnlyUrlRetrieval = 0x1000, disableWeakHash = 0x2000;
        var action = VerifyAuthenticode;
        var subject = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        try
        {
            Marshal.StructureToPtr(file, subject, false);
            var data = new TrustData
            {
                Size = (uint)Marshal.SizeOf<TrustData>(),
                UIChoice = 2, // WTD_UI_NONE
                UnionChoice = 1, // WTD_CHOICE_FILE
                File = subject,
                StateAction = verify,
                ProviderFlags = cacheOnlyUrlRetrieval | disableWeakHash
            };
            try
            {
                if (WinVerifyTrust(-1, ref action, ref data) != 0 || data.State == 0)
                    return false;
                var provider = WTHelperProvDataFromStateData(data.State);
                var signer = provider == 0 ? 0 : WTHelperGetProvSignerFromChain(provider, 0, false, 0);
                var info = signer == 0 ? 0 : WTHelperGetProvCertFromChain(signer, 0);
                if (info == 0)
                    return false;

                var context = Marshal.PtrToStructure<ProviderCertificate>(info).Context;
                if (context == 0)
                    return false;
                using var certificate = new X509Certificate2(context);
                return certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>()
                    .Any(usage => usage.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == PublisherUsage)) &&
                    HasPublisherRoot(signer);
            }
            catch (CryptographicException)
            {
                return false;
            }
            finally
            {
                data.StateAction = close;
                _ = WinVerifyTrust(-1, ref action, ref data);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(subject);
        }
    }

    private static bool HasPublisherRoot(nint signer)
    {
        // Use the Authenticode provider's chain so timestamped binaries remain
        // valid after the short-lived subscriber certificate expires.
        for (uint i = 1; i < 16; i++)
        {
            var info = WTHelperGetProvCertFromChain(signer, i);
            if (info == 0)
                return false;
            var entry = Marshal.PtrToStructure<ProviderCertificate>(info);
            if (entry.Context == 0)
                return false;
            if (WTHelperGetProvCertFromChain(signer, i + 1) != 0)
                continue;
            using var root = new X509Certificate2(entry.Context);
            return root.GetCertHashString(HashAlgorithmName.SHA256) == PublisherRoot;
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustFile
    {
        internal uint Size;
        internal nint Path, File, KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        internal uint Size;
        internal nint Policy, Client;
        internal uint UIChoice, RevocationChecks, UnionChoice;
        internal nint File;
        internal uint StateAction;
        internal nint State, UrlReference;
        internal uint ProviderFlags, UIContext;
        internal nint SignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderCertificate
    {
        internal uint Size;
        internal nint Context;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(nint window, ref Guid action, ref TrustData data);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperProvDataFromStateData(nint state);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperGetProvSignerFromChain(nint provider, uint signer, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counter);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperGetProvCertFromChain(nint signer, uint certificate);
}
