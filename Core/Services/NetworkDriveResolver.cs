using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SwiftList.Core
{
    public class ResolvedNetworkDrive
    {
        public string Letter { get; set; } = string.Empty;
        public string UncPath { get; set; } = string.Empty;
        public bool IsReady { get; set; }
    }

    public static class NetworkDriveResolver
    {
        /// <summary>
        /// Returns all network drives known to the current user session.
        /// </summary>
        public static List<ResolvedNetworkDrive> GetNetworkDrives()
        {
            var results = new List<ResolvedNetworkDrive>();

            try
            {
                foreach (var d in DriveInfo.GetDrives())
                {
                    if (d.DriveType == DriveType.Network)
                    {
                        string letter = d.Name.Split(':')[0].ToUpperInvariant();

                        results.Add(new ResolvedNetworkDrive
                        {
                            Letter = letter,
                            UncPath = string.Empty,
                            IsReady = d.IsReady
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[NetworkDriveResolver] Failed to get network drives: {ex.Message}", LogLevel.Error);
            }

            return results.OrderBy(d => d.Letter).ToList();
        }
    }
}
