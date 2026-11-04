using System;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace StrategyDirectorMod
{
    /// <summary>
    /// 0.4.3: the game overwrites MM_Data\output_log.txt every time it starts, so a weekend's log
    /// was lost whenever the game was closed before copying it. This keeps a copy per game launch,
    /// refreshed after every session (practice, qualifying, race) and when the game quits:
    ///   Mods\StrategyDirector\logs\output_log yyyy-MM-dd HHmm.txt
    /// and the same file in Settings.CopyLogsToFolder (optional; set in the UMM panel) if it exists.
    /// </summary>
    internal static class LogArchive
    {
        private static readonly string sStamp = DateTime.Now.ToString("yyyy-MM-dd HHmm");
        private static bool sAnnounced;

        public static string FileName
        {
            get { return "output_log " + sStamp + ".txt"; }
        }

        public static void Save(string when)
        {
            try
            {
                if (Main.Settings == null || !Main.Settings.SaveLogCopies || Main.Entry == null)
                {
                    return;
                }
                string src = Path.Combine(Application.dataPath, "output_log.txt");
                if (!File.Exists(src))
                {
                    return;
                }
                byte[] data;
                using (FileStream fs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    data = new byte[fs.Length];
                    int read = 0;
                    while (read < data.Length)
                    {
                        int n = fs.Read(data, read, data.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    if (read < data.Length)
                    {
                        Array.Resize(ref data, read);
                    }
                }
                string local = Path.Combine(Main.Entry.Path, "logs");
                Directory.CreateDirectory(local);
                File.WriteAllBytes(Path.Combine(local, FileName), data);
                string extra = Main.Settings.CopyLogsToFolder;
                bool copied = false;
                if (!string.IsNullOrEmpty(extra) && Directory.Exists(extra))
                {
                    File.WriteAllBytes(Path.Combine(extra, FileName), data);
                    copied = true;
                }
                if (!sAnnounced)
                {
                    sAnnounced = true;
                    Main.Log(string.Format("LOG: saving a copy of this game's log as \"{0}\" in Mods\\StrategyDirector\\logs{1} after every session and at quit.",
                        FileName, copied ? " and in " + extra : ""));
                }
            }
            catch (Exception e)
            {
                Main.Error("Saving a log copy failed (" + when + "): " + e.Message);
            }
        }
    }

    [HarmonyPatch(typeof(RaceDirector), "OnSessionEnd")]
    internal static class LogArchiveSessionEndPatch
    {
        private static void Postfix()
        {
            LogArchive.Save("session end");
        }
    }
}
