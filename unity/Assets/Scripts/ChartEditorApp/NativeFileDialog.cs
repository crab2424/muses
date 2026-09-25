using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Muses.ChartTool
{
    /// <summary>
    /// OSネイティブのファイル/フォルダ選択ダイアログ。従来はスタンドアロンにネイティブAPIが無い前提で
    /// 自前ブラウザ(ShowFileModal等)を使っていたが、ネイティブプラグインのビルドを増やさずに
    /// 呼べる経路があるため、それを優先し自前ブラウザは失敗時の代替に回す。
    /// - macOS: osascript の choose file / choose folder（外部プロセス。Xcodeでのバンドルビルド不要）。
    /// - Windows: IFileOpenDialog（COM、ole32/shell32のP/Invokeのみ）。STAの専用スレッドで開く。
    /// どちらもメインスレッドを塞がず、結果は呼び出し時のSynchronizationContext（Unityのメインスレッド）
    /// へ戻してからコールバックする。
    /// </summary>
    public static class NativeFileDialog
    {
        public enum Status { Picked, Cancelled, Failed }

        public readonly struct Result
        {
            public readonly Status status;
            public readonly string path;
            public readonly string error;
            public Result(Status status, string path = null, string error = null)
            {
                this.status = status;
                this.path = path;
                this.error = error;
            }
        }

        private static bool IsMac =>
            Application.platform == RuntimePlatform.OSXPlayer || Application.platform == RuntimePlatform.OSXEditor;
        private static bool IsWindows =>
            Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.WindowsEditor;

        public static bool IsSupported => IsMac || IsWindows;

        /// <summary>ダイアログを開いている最中か。二重に開かないためのガード。</summary>
        public static bool IsOpen { get; private set; }

        /// <summary>macOSでは外部プロセスを殺して閉じられる。Windowsのダイアログは自身が
        /// オーナーウィンドウを無効化してモーダルになるため、こちらから閉じる手段は用意しない。</summary>
        public static bool CanCancel => IsMac;

        private static volatile Process macProcess;
        private static volatile bool cancelRequested;

        /// <param name="extensions">ドット無しの拡張子（例: "muses", "ogg"）。空なら全ファイル。</param>
        public static void OpenFile(string title, string startDir, string[] extensions, Action<Result> onDone) =>
            Start(title, startDir, extensions, pickFolder: false, onDone);

        public static void PickFolder(string title, string startDir, Action<Result> onDone) =>
            Start(title, startDir, null, pickFolder: true, onDone);

        public static void CancelPending()
        {
            cancelRequested = true;
            try
            {
                if (macProcess != null && !macProcess.HasExited) macProcess.Kill();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"NativeFileDialog: 中断に失敗しました: {ex.Message}");
            }
        }

        private static void Start(string title, string startDir, string[] extensions, bool pickFolder, Action<Result> onDone)
        {
            if (!IsSupported) { onDone(new Result(Status.Failed, error: "未対応のOSです")); return; }
            if (IsOpen) { onDone(new Result(Status.Failed, error: "ダイアログが既に開いています")); return; }

            if (string.IsNullOrEmpty(startDir) || !Directory.Exists(startDir)) startDir = "";
            extensions ??= Array.Empty<string>();
            foreach (var e in extensions)
                if (!Regex.IsMatch(e, "^[A-Za-z0-9]+$"))
                    throw new ArgumentException($"拡張子に使えない文字が含まれています: {e}");

            var ctx = SynchronizationContext.Current;
            IntPtr owner = IsWindows ? Win.GetActiveWindow() : IntPtr.Zero; // メインスレッドで取る
            IsOpen = true;
            cancelRequested = false;

            void Finish(Result r)
            {
                void Deliver()
                {
                    IsOpen = false;
                    macProcess = null;
                    onDone(r);
                }
                if (ctx != null) ctx.Post(_ => Deliver(), null);
                else Deliver();
            }

            var thread = new Thread(() =>
            {
                Result r;
                try
                {
                    r = IsMac
                        ? RunMac(title, startDir, extensions, pickFolder)
                        : Win.Run(owner, title, startDir, extensions, pickFolder);
                }
                catch (Exception ex)
                {
                    r = new Result(Status.Failed, error: ex.Message);
                }
                Finish(r);
            })
            { IsBackground = true, Name = "NativeFileDialog" };
            if (IsWindows) thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        // ---------------- macOS ----------------

        private static Result RunMac(string title, string startDir, string[] extensions, bool pickFolder)
        {
            // プロンプト文字列・初期フォルダはスクリプトへ埋め込まずargvで渡す（エスケープ事故を避ける）。
            // 拡張子だけは英数字に制限済みなのでリテラルとして埋め込む。
            // `activate` は osascript 自身を前面に出すだけで、他アプリへのApple Eventを送らないため
            // オートメーション許可のダイアログは出ない。
            string cmd = pickFolder ? "choose folder" : "choose file";
            string typeClause = !pickFolder && extensions.Length > 0
                ? " of type {" + string.Join(",", Array.ConvertAll(extensions, e => "\"" + e + "\"")) + "}"
                : "";
            string[] lines =
            {
                "on run argv",
                "activate",
                "set promptText to item 1 of argv",
                "set startDir to item 2 of argv",
                "if startDir is \"\" then",
                $"set picked to {cmd} with prompt promptText{typeClause}",
                "else",
                $"set picked to {cmd} with prompt promptText{typeClause} default location (POSIX file startDir)",
                "end if",
                "return POSIX path of picked",
                "end run",
            };

            var args = new StringBuilder();
            foreach (var l in lines) args.Append("-e ").Append(Quote(l)).Append(' ');
            args.Append(Quote(title ?? "")).Append(' ').Append(Quote(startDir));

            var psi = new ProcessStartInfo("/usr/bin/osascript", args.ToString())
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p == null) return new Result(Status.Failed, error: "osascriptを起動できませんでした");
            macProcess = p;
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();

            if (p.ExitCode == 0)
            {
                string path = stdout.TrimEnd('\n', '\r');
                // choose folder の POSIX path は末尾に / が付く。
                if (path.Length > 1) path = path.TrimEnd('/');
                return string.IsNullOrEmpty(path)
                    ? new Result(Status.Cancelled)
                    : new Result(Status.Picked, path);
            }
            // -128 = ユーザーによるキャンセル。CancelPending()でKillした場合もキャンセル扱い。
            if (cancelRequested || stderr.Contains("-128"))
                return new Result(Status.Cancelled);
            return new Result(Status.Failed, error: stderr.Trim());
        }

        /// <summary>Mono(Unix)のArguments解釈に合わせたクォート。二重引用符とバックスラッシュだけを逃がす。</summary>
        private static string Quote(string s) =>
            "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        // ---------------- Windows ----------------

        private static class Win
        {
            [DllImport("user32.dll")]
            public static extern IntPtr GetActiveWindow();

            [DllImport("ole32.dll")]
            private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

            [DllImport("ole32.dll")]
            private static extern void CoUninitialize();

            [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
            private static extern void SHCreateItemFromParsingName(
                [MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr pbc, [In] ref Guid riid,
                [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

            private const uint COINIT_APARTMENTTHREADED = 0x2;
            private const uint FOS_NOCHANGEDIR = 0x8;
            private const uint FOS_PICKFOLDERS = 0x20;
            private const uint FOS_FORCEFILESYSTEM = 0x40;
            private const uint FOS_PATHMUSTEXIST = 0x800;
            private const uint FOS_FILEMUSTEXIST = 0x1000;
            private const uint SIGDN_FILESYSPATH = 0x80058000;
            private const int HRESULT_CANCELLED = unchecked((int)0x800704C7);

            public static Result Run(IntPtr owner, string title, string startDir, string[] extensions, bool pickFolder)
            {
                int hrInit = CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);
                try
                {
                    var dlg = (IFileOpenDialog)new FileOpenDialogRCW();
                    try
                    {
                        dlg.GetOptions(out uint opts);
                        opts |= FOS_NOCHANGEDIR | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST;
                        opts |= pickFolder ? FOS_PICKFOLDERS : FOS_FILEMUSTEXIST;
                        dlg.SetOptions(opts);
                        if (!string.IsNullOrEmpty(title)) dlg.SetTitle(title);

                        if (!pickFolder && extensions.Length > 0)
                        {
                            string spec = string.Join(";", Array.ConvertAll(extensions, e => "*." + e));
                            dlg.SetFileTypes(1, new[] { new COMDLG_FILTERSPEC { pszName = spec, pszSpec = spec } });
                        }

                        if (!string.IsNullOrEmpty(startDir))
                        {
                            try
                            {
                                var iid = typeof(IShellItem).GUID;
                                SHCreateItemFromParsingName(startDir, IntPtr.Zero, ref iid, out var folder);
                                dlg.SetFolder(folder);
                            }
                            catch
                            {
                                // 初期フォルダが設定できなくても選択自体は続行する。
                            }
                        }

                        int hr = dlg.Show(owner);
                        if (hr == HRESULT_CANCELLED) return new Result(Status.Cancelled);
                        if (hr != 0) return new Result(Status.Failed, error: $"IFileOpenDialog.Show: 0x{hr:X8}");

                        dlg.GetResult(out var item);
                        item.GetDisplayName(SIGDN_FILESYSPATH, out string path);
                        return new Result(Status.Picked, path);
                    }
                    finally
                    {
                        Marshal.FinalReleaseComObject(dlg);
                    }
                }
                finally
                {
                    if (hrInit >= 0) CoUninitialize();
                }
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            private struct COMDLG_FILTERSPEC
            {
                [MarshalAs(UnmanagedType.LPWStr)] public string pszName;
                [MarshalAs(UnmanagedType.LPWStr)] public string pszSpec;
            }

            [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
            private class FileOpenDialogRCW { }

            // vtableの並び順がそのままCOMの呼び出し先になるため、宣言順を変えないこと
            // （IModalWindow::Show → IFileDialog の各メソッド → IFileOpenDialog の2メソッド）。
            [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            private interface IFileOpenDialog
            {
                [PreserveSig] int Show(IntPtr parent);
                void SetFileTypes(uint cFileTypes, [MarshalAs(UnmanagedType.LPArray)] COMDLG_FILTERSPEC[] rgFilterSpec);
                void SetFileTypeIndex(uint iFileType);
                void GetFileTypeIndex(out uint piFileType);
                void Advise(IntPtr pfde, out uint pdwCookie);
                void Unadvise(uint dwCookie);
                void SetOptions(uint fos);
                void GetOptions(out uint pfos);
                void SetDefaultFolder(IShellItem psi);
                void SetFolder(IShellItem psi);
                void GetFolder(out IShellItem ppsi);
                void GetCurrentSelection(out IShellItem ppsi);
                void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
                void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
                void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
                void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
                void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
                void GetResult(out IShellItem ppsi);
                void AddPlace(IShellItem psi, int fdap);
                void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
                void Close(int hr);
                void SetClientGuid([In] ref Guid guid);
                void ClearClientData();
                void SetFilter(IntPtr pFilter);
                void GetResults(out IntPtr ppenum);
                void GetSelectedItems(out IntPtr ppsai);
            }

            [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            private interface IShellItem
            {
                void BindToHandler(IntPtr pbc, [In] ref Guid bhid, [In] ref Guid riid, out IntPtr ppv);
                void GetParent(out IShellItem ppsi);
                void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
                void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
                void Compare(IShellItem psi, uint hint, out int piOrder);
            }
        }
    }
}
