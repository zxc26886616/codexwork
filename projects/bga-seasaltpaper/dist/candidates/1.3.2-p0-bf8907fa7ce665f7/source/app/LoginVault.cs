using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SeaSaltDesk;

public enum LoginSite { BGA, Studio }

public sealed class LoginSecret(string user, string password)
{
    public string User { get; } = user;
    public string Password { get; } = password;
}

public sealed class LoginVault(string root)
{
    private const int Generic = 1, LocalMachine = 2, NotFound = 1168;
    private readonly string settings = Path.Combine(root, "login-site.txt");
    private readonly string prefix = "SeaSaltDesk/" + Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(Path.GetFullPath(root).TrimEnd('\\').ToUpperInvariant())))[..16] + "/";

    public LoginSite? Preferred => File.Exists(settings) && Enum.TryParse<LoginSite>(File.ReadAllText(settings).Trim(), out var site)
        && Enum.IsDefined(site) ? site : null;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string Target;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint Size;
        public IntPtr Blob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes, Alias;
        public string User;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWriteW(ref Credential value, uint flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredReadW(string target, uint type, uint flags, out IntPtr ptr);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDeleteW(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr ptr);

    public LoginSecret? Read(LoginSite site)
    {
        if (!CredReadW(prefix + site, Generic, 0, out var ptr))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == NotFound) return null;
            throw new Win32Exception(error);
        }
        try
        {
            var value = Marshal.PtrToStructure<Credential>(ptr);
            string password = Marshal.PtrToStringUni(value.Blob, checked((int)value.Size / 2)) ?? "";
            return new(value.User, password);
        }
        finally { CredFree(ptr); }
    }

    public void Save(LoginSite site, LoginSecret secret)
    {
        if (string.IsNullOrWhiteSpace(secret.User) || string.IsNullOrEmpty(secret.Password) ||
            secret.Password.Length > 1280) throw new ArgumentException("请检查用户名与密码。");
        var ptr = Marshal.StringToCoTaskMemUni(secret.Password);
        try
        {
            var value = new Credential { Type = Generic, Target = prefix + site, User = secret.User,
                Blob = ptr, Size = (uint)(secret.Password.Length * 2), Persist = LocalMachine };
            if (!CredWriteW(ref value, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.ZeroFreeCoTaskMemUnicode(ptr); }
        Directory.CreateDirectory(root);
        string tmp = Path.Combine(root, Guid.NewGuid() + ".tmp");
        try { File.WriteAllText(tmp, site.ToString()); File.Move(tmp, settings, true); }
        finally { File.Delete(tmp); }
    }

    public void Forget(LoginSite site)
    {
        if (!CredDeleteW(prefix + site, Generic, 0) && Marshal.GetLastWin32Error() != NotFound)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (Preferred == site) File.Delete(settings);
    }
}
