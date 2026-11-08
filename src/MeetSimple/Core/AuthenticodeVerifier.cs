using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MeetSimple.Core;

/// <summary>Checks a Windows file's digital signature (chain and file hash) and who signed it, before the app runs it.</summary>
public static class AuthenticodeVerifier
{
    private const uint UiNone = 2;
    private const uint RevokeNone = 0;
    private const uint ChoiceFile = 1;
    private const uint StateActionIgnore = 0;
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static bool IsSignedBy(string filePath, string expectedOrganization, out string reason)
    {
        reason = "";
        if (!HasValidSignature(filePath))
        {
            reason = "its digital signature is missing or invalid";
            return false;
        }

        try
        {
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
            if (!SubjectHasOrganization(certificate.Subject, expectedOrganization))
            {
                reason = $"it is signed by someone else ({certificate.GetNameInfo(X509NameType.SimpleName, false)})";
                return false;
            }
            return true;
        }
        catch (CryptographicException)
        {
            reason = "its signing certificate could not be read";
            return false;
        }
    }

    private static bool SubjectHasOrganization(string subject, string organization)
    {
        foreach (var part in subject.Split(','))
        {
            var pieces = part.Trim().Split('=', 2);
            if (pieces.Length == 2
                && (pieces[0].Equals("O", StringComparison.OrdinalIgnoreCase) || pieces[0].Equals("CN", StringComparison.OrdinalIgnoreCase))
                && pieces[1].Trim().Trim('"').Equals(organization, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasValidSignature(string filePath)
    {
        var fileInfo = new WinTrustFileInfo
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            FilePath = filePath,
        };
        IntPtr fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            var trustData = new WinTrustData
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = UiNone,
                RevocationChecks = RevokeNone,
                UnionChoice = ChoiceFile,
                FileInfoPointer = fileInfoPointer,
                StateAction = StateActionIgnore,
            };
            Guid action = GenericVerifyV2;
            int result = WinVerifyTrust(new IntPtr(-1), ref action, ref trustData);
            return result == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
            Marshal.FreeHGlobal(fileInfoPointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfoPointer;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr windowHandle, ref Guid actionId, ref WinTrustData trustData);
}
