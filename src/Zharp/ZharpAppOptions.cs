using System;

namespace Zharp
{
    /// <summary>Settings only apps have (Zharp.Windows, Zharp.Maui).</summary>
    public sealed class ZharpAppOptions
    {
        /// <summary>Report the UI thread not answering for <see cref="HangThreshold"/>. Default on.</summary>
        public bool DetectHangs { get; set; } = true;

        /// <summary>How long the UI thread must be blocked to be a hang. Default 5 s, like Android's ANR.</summary>
        public TimeSpan HangThreshold { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>Each window, page and form shown is a screen (<c>screen.name</c>). Default on.</summary>
        public bool TrackScreens { get; set; } = true;
    }
}
