using System;
using System.Runtime.InteropServices;

namespace DeepEngineers.Wire.Native
{
    /// <summary>Raw advapi32 P/Invoke: SDDL → security descriptor, and the current user's SID from the process token.</summary>
    internal static class Advapi32
    {
        public const uint SDDL_REVISION_1 = 1;
        public const uint TOKEN_QUERY = 0x0008;
        /// <summary>TOKEN_INFORMATION_CLASS.TokenUser.</summary>
        public const int TokenUser = 1;

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string stringSecurityDescriptor,
            uint stringSdRevision, out IntPtr securityDescriptor, IntPtr securityDescriptorSize);

        [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation,
            uint tokenInformationLength, out uint returnLength);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr stringSid);
    }
}
