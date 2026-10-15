using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace CleanSweep.Engine
{
    /// <summary>
    /// Authenticode (digital signature) checks, the same check Windows does before showing "Verified publisher".
    /// Used by the updater: once CleanSweep is signed, an update must be validly signed by the same publisher.
    /// Works offline (no revocation lookups), so a check never hangs on a slow network.
    /// </summary>
    public static class Signing
    {
        public class Info
        {
            public bool Signed, Trusted; public string Publisher = "", Subject = "", Thumbprint = "", Problem = "";
            public override string ToString() => !Signed ? "not signed" : Trusted ? $"signed by {Publisher}" : $"signature not valid ({Problem})";
        }

        const uint TRUST_E_NOSIGNATURE = 0x800B0100, TRUST_E_SUBJECT_FORM_UNKNOWN = 0x800B0003, TRUST_E_PROVIDER_UNKNOWN = 0x800B0001,
                   CERT_E_UNTRUSTEDROOT = 0x800B0109, TRUST_E_BAD_DIGEST = 0x80096010, CERT_E_EXPIRED = 0x800B0101, CRYPT_E_SECURITY_SETTINGS = 0x80092026,
                   TRUST_E_EXPLICIT_DISTRUST = 0x800B0111, CERT_E_CHAINING = 0x800B010A;

        public static Info Check(string file)
        {
            var r = new Info();
            uint hr;
            try { hr = Verify(file); } catch (Exception e) { r.Problem = e.Message; return r; }
            if (hr == TRUST_E_NOSIGNATURE || hr == TRUST_E_SUBJECT_FORM_UNKNOWN || hr == TRUST_E_PROVIDER_UNKNOWN) return r;
            r.Signed = true; r.Trusted = hr == 0;
            if (!r.Trusted)
                r.Problem = hr == TRUST_E_BAD_DIGEST ? "the file was changed after it was signed"
                    : hr == CERT_E_UNTRUSTEDROOT || hr == CERT_E_CHAINING ? "the certificate is not trusted on this PC"
                    : hr == CERT_E_EXPIRED ? "the certificate has expired and the signature has no timestamp"
                    : hr == TRUST_E_EXPLICIT_DISTRUST ? "the certificate is blocked"
                    : hr == CRYPT_E_SECURITY_SETTINGS ? "blocked by this PC's security settings"
                    : $"error 0x{hr:X8}";
            try
            {
                using (var c = new X509Certificate2(X509Certificate.CreateFromSignedFile(file)))
                {
                    r.Subject = c.Subject; r.Thumbprint = c.Thumbprint;
                    r.Publisher = c.GetNameInfo(X509NameType.SimpleName, false);
                }
            }
            catch { }
            return r;
        }

        /// <summary>null = the update may be installed, otherwise why not. <paramref name="current"/> is this copy of CleanSweep.</summary>
        public static string UpdateProblem(string current, string update)
        {
            var mine = Check(current); var theirs = Check(update);
            if (theirs.Signed && !theirs.Trusted) return $"The download's digital signature is not valid ({theirs.Problem}), so it was not installed.";
            if (mine.Signed && mine.Trusted)
            {
                if (!theirs.Signed) return "This copy of CleanSweep is digitally signed but the download is not, so it was not installed.";
                if (!string.Equals(theirs.Subject, mine.Subject, StringComparison.OrdinalIgnoreCase))
                    return $"The download is signed by a different publisher ({theirs.Publisher}), so it was not installed.";
            }
            return null;
        }

        // ------------------------------------------------------------------ WinVerifyTrust
        static readonly Guid GenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WINTRUST_FILE_INFO { public uint cbStruct; public string pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject; }

        [StructLayout(LayoutKind.Sequential)]
        struct WINTRUST_DATA
        {
            public uint cbStruct; public IntPtr pPolicyCallbackData, pSIPClientData; public uint dwUIChoice, fdwRevocationChecks, dwUnionChoice;
            public IntPtr pFile; public uint dwStateAction; public IntPtr hWVTStateData; public IntPtr pwszURLReference; public uint dwProvFlags, dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        static extern uint WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WINTRUST_DATA data);

        static uint Verify(string file)
        {
            var fi = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)), pcwszFilePath = file };
            IntPtr pf = Marshal.AllocHGlobal(Marshal.SizeOf(fi));
            try
            {
                Marshal.StructureToPtr(fi, pf, false);
                var d = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_DATA)), dwUIChoice = 2 /* WTD_UI_NONE */, fdwRevocationChecks = 0 /* WTD_REVOKE_NONE */,
                    dwUnionChoice = 1 /* WTD_CHOICE_FILE */, pFile = pf, dwStateAction = 1 /* WTD_STATEACTION_VERIFY */,
                    dwProvFlags = 0x1000 /* WTD_CACHE_ONLY_URL_RETRIEVAL */ | 0x80 /* WTD_REVOCATION_CHECK_NONE */
                };
                uint hr = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref d);
                d.dwStateAction = 2; /* WTD_STATEACTION_CLOSE */ WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref d);
                return hr;
            }
            finally { Marshal.FreeHGlobal(pf); }
        }
    }
}
