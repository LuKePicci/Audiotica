﻿namespace Audiotica.Core.Utilities.Interfaces
{
    public interface IAppSettingsUtility
    {
        string DownloadsPath { get; set; }
        string TempDownloadsPath { get; }
    }
}