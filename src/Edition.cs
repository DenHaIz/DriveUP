namespace DriveUp
{
    /// <summary>
    /// DriveUP and DriveUP Pro are built from the same sources: build.cmd compiles DriveUP_Pro.exe
    /// with /define:PRO and adds the files in src\Pro (system tuning). Everything else is shared.
    /// </summary>
    static class Edition
    {
#if PRO
        public const bool Pro = true;
        /// <summary>Shown in the header and the window title.</summary>
        public const string Name = "DriveUP Pro";
        /// <summary>File names: the exe, the .ini and the journal; the single-instance lock.</summary>
        public const string Id = "DriveUP_Pro";
#else
        public const bool Pro = false;
        public const string Name = "DriveUP";
        public const string Id = "DriveUP";
#endif
    }
}
