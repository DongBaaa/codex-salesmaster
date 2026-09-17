using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace GeoraePlanInstaller
{
    public sealed class CachedMsiSource : IDisposable
    {
        private FileStream lease;
        public string Path { get; private set; }
        public long Length
        {
            get
            {
                if (lease == null) throw new ObjectDisposedException("CachedMsiSource");
                return lease.Length;
            }
        }

        internal CachedMsiSource(string path)
        {
            // Keep read access available to MSI, but prevent replacement/deletion
            // until the worker has finished its transaction.
            lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Path = path;
        }

        public void Dispose()
        {
            if (lease != null) { lease.Dispose(); lease = null; }
        }
    }

    public sealed class InstalledMsiProduct
    {
        public string ProductCode { get; set; }
        public string Version { get; set; }
        public string InstallRoot { get; set; }
    }

    public sealed class MsiTransaction : IDisposable
    {
        private uint handle;
        private IntPtr ownerChanged;
        private bool completed;

        internal MsiTransaction(uint handle, IntPtr ownerChanged)
        {
            this.handle = handle;
            this.ownerChanged = ownerChanged;
        }

        public void Install(string msiPath, string installRoot, bool createShortcuts)
        {
            if (handle == 0 || completed) throw new InvalidOperationException("MSI transaction is not active.");
            string path = NativeMsiRuntime.CanonicalDirectory(installRoot);
            if (path.IndexOf('"') >= 0 || path.IndexOf('\r') >= 0 || path.IndexOf('\n') >= 0)
                throw new InvalidOperationException("Invalid MSI installation path.");
            if (!Path.IsPathRooted(msiPath) || !File.Exists(msiPath))
                throw new InvalidOperationException("MSI package must be an existing absolute file path.");
            NativeMsiRuntime.Check(NativeMsiRuntime.MsiInstallProductW(msiPath,
                "REBOOT=ReallySuppress MSIRESTARTMANAGERCONTROL=Disable INSTALL_SHORTCUTS=" +
                (createShortcuts ? "1" : "0") + " INSTALLFOLDER=\"" + path + "\""), "install");
        }

        public void Commit()
        {
            if (handle == 0 || completed) throw new InvalidOperationException("MSI transaction is not active.");
            NativeMsiRuntime.Check(NativeMsiRuntime.MsiEndTransaction(1), "commit");
            completed = true;
        }

        public void Dispose()
        {
            if (handle == 0) return;
            // Even when a failed commit has an uncertain outcome, attempt to
            // roll back. The caller must use a fresh engine barrier and query
            // registration before deciding whether file restoration is legal.
            try
            {
                if (!completed) NativeMsiRuntime.Check(NativeMsiRuntime.MsiEndTransaction(0), "rollback");
            }
            finally
            {
                NativeMsiRuntime.MsiCloseHandle(handle);
                handle = 0;
                if (ownerChanged != IntPtr.Zero)
                {
                    NativeMsiRuntime.CloseHandle(ownerChanged);
                    ownerChanged = IntPtr.Zero;
                }
            }
        }
    }

    public static class NativeMsiRuntime
    {
        [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint MsiBeginTransactionW(string name, uint attributes, out uint transaction, out IntPtr ownerChanged);
        [DllImport("msi.dll", ExactSpelling = true)]
        internal static extern uint MsiEndTransaction(uint state);
        [DllImport("msi.dll", ExactSpelling = true)]
        internal static extern uint MsiCloseHandle(uint handle);
        [DllImport("kernel32.dll", ExactSpelling = true)]
        internal static extern bool CloseHandle(IntPtr handle);
        [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern uint MsiInstallProductW(string path, string properties);
        [DllImport("msi.dll", ExactSpelling = true)]
        private static extern uint MsiSetInternalUI(uint level, IntPtr owner);
        [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint MsiEnumRelatedProductsW(string upgradeCode, uint reserved, uint index, StringBuilder code);
        [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint MsiGetProductInfoW(string product, string property, StringBuilder value, ref uint length);
        [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int MsiQueryProductStateW(string product);

        internal static void Check(uint result, string action)
        {
            if (result != 0) throw new InvalidOperationException("Windows Installer " + action + " failed: " + result);
        }

        public static uint SetSilentUI() { return MsiSetInternalUI(2, IntPtr.Zero); }
        public static void RestoreUI(uint previous) { MsiSetInternalUI(previous, IntPtr.Zero); }

        // Windows caches the MSI database, but not necessarily its embedded CAB.
        // Install from a durable, administrator-owned source so ordinary repair
        // still works after the updater deletes its extraction directory.
        public static string CachePackage(string source, string productCode, string expectedHash, long expectedLength)
        {
            using (CachedMsiSource cached = AcquireCachedPackage(source, productCode, expectedHash, expectedLength))
                return cached.Path;
        }

        public static CachedMsiSource AcquireCachedPackage(string source, string productCode, string expectedHash, long expectedLength)
        {
            if (expectedLength <= 0 || expectedHash == null || expectedHash.Length != 64)
                throw new InvalidOperationException("Invalid native source descriptor.");
            foreach (char ch in expectedHash)
                if (!Uri.IsHexDigit(ch)) throw new InvalidOperationException("Invalid native source hash.");
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("Machine MSI source caching requires an administrator.");
            string parent = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (String.IsNullOrWhiteSpace(parent)) parent = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            parent = CanonicalDirectory(parent);
            AssertNoReparseChain(parent);
            AssertCacheParentSecurity(parent);
            string root = Path.Combine(parent, "TradePlanInstallerCache");
            if (!Directory.Exists(root)) Directory.CreateDirectory(root, CacheDirectorySecurity());
            AssertCacheSecurity(root, true);
            // Serialize publication and the lease handoff across worker processes.
            // Any future cache maintenance must acquire this same gate and must
            // not unlink sources with an outstanding read lease.
            using (FileStream gate = AcquireCacheGate(root))
            {
                string destination = CachePackageCore(source, productCode, expectedHash, expectedLength, root);
                return new CachedMsiSource(destination);
            }
        }

        private static FileStream AcquireCacheGate(string root)
        {
            string path = Path.Combine(root, ".cache.lock");
            System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                AssertCacheSecurity(root, true);
                if (File.Exists(path)) AssertCacheSecurity(path, false);
                FileStream gate = null;
                try
                {
                    gate = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    AssertCacheSecurity(path, false);
                    return gate;
                }
                catch (IOException ex)
                {
                    if (gate != null) gate.Dispose();
                    int error = ex.HResult & 0xffff;
                    if ((error != 32 && error != 33) || timer.ElapsedMilliseconds >= 30000) throw;
                    Thread.Sleep(50);
                }
                catch
                {
                    if (gate != null) gate.Dispose();
                    throw;
                }
            }
        }

        private static string CachePackageCore(string source, string productCode, string expectedHash, long expectedLength, string root)
        {
            string product = Path.Combine(root, Guid.Parse(productCode).ToString("N"));
            string folder = Path.Combine(product, expectedHash.ToLowerInvariant());
            foreach (string path in new string[] { product, folder })
            {
                if (!Directory.Exists(path)) Directory.CreateDirectory(path, CacheDirectorySecurity());
                AssertCacheSecurity(path, true);
            }
            string destination = Path.Combine(folder, "installer.msi");
            if (!File.Exists(destination))
            {
                string temporary = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".partial");
                try
                {
                    AssertNoReparseChain(source);
                    using (FileStream input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (FileStream output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                        FileShare.None, 65536, FileOptions.WriteThrough))
                    {
                        if (input.Length != expectedLength) throw new InvalidOperationException("MSI source length changed.");
                        input.CopyTo(output);
                        output.Flush(true);
                    }
                    AssertCachedContent(temporary, expectedHash, expectedLength);
                    File.Move(temporary, destination);
                }
                finally
                {
                    if (File.Exists(temporary))
                    {
                        AssertCacheSecurity(temporary, false);
                        File.Delete(temporary);
                    }
                }
            }
            AssertCachedContent(destination, expectedHash, expectedLength);
            return destination;
        }

        private static DirectorySecurity CacheDirectorySecurity()
        {
            DirectorySecurity security = new DirectorySecurity();
            security.SetOwner(new SecurityIdentifier("S-1-5-32-544"));
            security.SetAccessRuleProtection(true, false);
            InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            foreach (string sid in new string[] { "S-1-5-32-544", "S-1-5-18" })
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl,
                    inheritance, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-545"), FileSystemRights.ReadAndExecute,
                inheritance, PropagationFlags.None, AccessControlType.Allow));
            return security;
        }

        private static void AssertCacheParentSecurity(string path)
        {
            DirectorySecurity security = Directory.GetAccessControl(path);
            HashSet<string> trusted = new HashSet<string>(new string[] { "S-1-5-32-544", "S-1-5-18",
                "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464" }, StringComparer.Ordinal);
            if (!trusted.Contains(security.GetOwner(typeof(SecurityIdentifier)).Value))
                throw new InvalidOperationException("MSI cache parent owner is not trusted.");
            FileSystemRights write = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
                FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow || (rule.FileSystemRights & write) == 0) continue;
                string sid = rule.IdentityReference.Value;
                if (sid == "S-1-3-0" && (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
                if (!trusted.Contains(sid)) throw new InvalidOperationException("MSI cache parent permits untrusted writes.");
            }
        }

        private static void AssertNoReparseChain(string path)
        {
            for (string current = Path.GetFullPath(path); !String.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("MSI source cannot traverse a reparse point.");
        }

        private static void AssertCacheSecurity(string path, bool directory)
        {
            AssertNoReparseChain(path);
            FileSystemSecurity security = directory ? (FileSystemSecurity)Directory.GetAccessControl(path) : File.GetAccessControl(path);
            string owner = security.GetOwner(typeof(SecurityIdentifier)).Value;
            if (owner != "S-1-5-32-544" && owner != "S-1-5-18")
                throw new InvalidOperationException("MSI cache owner is not trusted.");
            if (directory && !security.AreAccessRulesProtected)
                throw new InvalidOperationException("MSI cache directory inherits untrusted access rules.");
            HashSet<string> writers = new HashSet<string>(StringComparer.Ordinal);
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                string sid = rule.IdentityReference.Value;
                if (rule.AccessControlType != AccessControlType.Allow || (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0)
                    throw new InvalidOperationException("Unexpected MSI cache access rule.");
                if (sid == "S-1-5-32-544" || sid == "S-1-5-18")
                {
                    if ((rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl) writers.Add(sid);
                }
                else if (sid != "S-1-5-32-545" || (rule.FileSystemRights & ~(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize)) != 0)
                    throw new InvalidOperationException("Untrusted MSI cache write access.");
            }
            if (!writers.Contains("S-1-5-32-544") || !writers.Contains("S-1-5-18"))
                throw new InvalidOperationException("MSI cache lacks required administrator/system access.");
        }

        private static void AssertCachedContent(string path, string expectedHash, long expectedLength)
        {
            AssertCacheSecurity(path, false);
            using (FileStream input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (SHA256 sha = SHA256.Create())
                if (input.Length != expectedLength || !String.Equals(BitConverter.ToString(sha.ComputeHash(input)).Replace("-", ""),
                    expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Existing MSI cache differs from the bound package.");
        }

        public static MsiTransaction TryBegin()
        {
            uint handle;
            IntPtr ownerChanged;
            uint result = MsiBeginTransactionW("GeoraePlan." + Guid.NewGuid().ToString("N"), 0, out handle, out ownerChanged);
            if (result == 1618) return null;
            Check(result, "begin");
            return new MsiTransaction(handle, ownerChanged);
        }

        public static string CanonicalDirectory(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
                throw new InvalidOperationException("Installation path must be absolute.");
            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (full.Length <= Path.GetPathRoot(full).Length)
                throw new InvalidOperationException("A volume root is not an installation directory.");
            return full;
        }

        private static string ProductInfo(string code, string property)
        {
            uint size = 0;
            uint result = MsiGetProductInfoW(code, property, null, ref size);
            if (result != 0 && result != 234) Check(result, "query " + property);
            if (size > 32768) throw new InvalidOperationException("MSI property exceeds the expected bound.");
            size++;
            StringBuilder buffer = new StringBuilder((int)size);
            Check(MsiGetProductInfoW(code, property, buffer, ref size), "read " + property);
            return buffer.ToString();
        }

        public static InstalledMsiProduct ReadProduct(string productCode)
        {
            string code = Guid.Parse(productCode).ToString("B").ToUpperInvariant();
            int state = MsiQueryProductStateW(code);
            if (state == -1) return null;
            if (state != 5) throw new InvalidOperationException("MSI product is not in an installed state: " + state);
            if (ProductInfo(code, "AssignmentType") != "1")
                throw new InvalidOperationException("Only per-machine MSI products are supported.");
            string version = ProductInfo(code, "VersionString");
            System.Version.Parse(version);
            return new InstalledMsiProduct
            {
                ProductCode = code,
                Version = version,
                InstallRoot = CanonicalDirectory(ProductInfo(code, "InstallLocation"))
            };
        }

        public static InstalledMsiProduct FindAtRoot(string upgradeCode, string installRoot)
        {
            string root = CanonicalDirectory(installRoot);
            string upgrade = Guid.Parse(upgradeCode).ToString("B");
            InstalledMsiProduct match = null;
            // Keep this enumeration synchronous: the MSI enumeration contract
            // requires each incrementing index to stay on the same thread.
            for (uint index = 0; index < 64; index++)
            {
                StringBuilder code = new StringBuilder(39);
                uint result = MsiEnumRelatedProductsW(upgrade, 0, index, code);
                if (result == 259) return match;
                Check(result, "enumerate related products");
                InstalledMsiProduct product = ReadProduct(code.ToString());
                if (product == null) throw new InvalidOperationException("MSI registration changed during enumeration.");
                if (!String.Equals(product.InstallRoot, root, StringComparison.OrdinalIgnoreCase)) continue;
                if (match != null) throw new InvalidOperationException("Multiple MSI registrations match this installation directory.");
                match = product;
            }
            throw new InvalidOperationException("MSI related-product enumeration exceeds the expected bound.");
        }

        public static void AssertPayload(string installRoot, string[] relativePaths, long[] lengths, string[] hashes)
        {
            if (relativePaths == null || lengths == null || hashes == null || relativePaths.Length == 0 ||
                relativePaths.Length != lengths.Length || relativePaths.Length != hashes.Length)
                throw new InvalidOperationException("Invalid installed payload manifest.");
            string root = CanonicalDirectory(installRoot);
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < relativePaths.Length; index++)
            {
                string relative = relativePaths[index];
                if (String.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.IndexOf(':') >= 0 ||
                    !seen.Add(relative.Replace('\\', '/')))
                    throw new InvalidOperationException("Invalid or duplicate installed payload path.");
                foreach (string part in relative.Split(new char[] { '/', '\\' }))
                    if (part == ".." || part == "." || part.Length == 0)
                        throw new InvalidOperationException("Invalid installed payload path segment.");
                string path = Path.GetFullPath(Path.Combine(root, relative));
                if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Installed payload path escapes the installation directory.");
                for (string parent = path; !String.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                    if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidOperationException("Installed payload must not traverse a reparse point.");
                using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (SHA256 sha = SHA256.Create())
                {
                    string hash = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
                    if (lengths[index] < 0 || file.Length != lengths[index] || !String.Equals(hash, hashes[index], StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Installed payload differs from the package: " + relative);
                }
            }
        }
    }
}
