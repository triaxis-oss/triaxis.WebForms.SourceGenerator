using System;
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace triaxis.WebForms.MSBuildTasks
{
    /// <summary>
    /// Truncates each input file to zero bytes in place. Used at publish
    /// time to shrink the deployed markup corpus: <c>BuildManager</c> only
    /// checks for file existence once the <c>.compiled</c> sidecars place
    /// the app in non-updatable precompiled mode, so the published
    /// <c>.aspx</c> / <c>.ascx</c> / <c>.master</c> files can be empty —
    /// keeping source out of the deployment artifact and shrinking it.
    /// </summary>
    public sealed class StripMarkupFiles : Task
    {
        [Required] public ITaskItem[] Files { get; set; } = Array.Empty<ITaskItem>();

        public override bool Execute()
        {
            int count = 0;
            foreach (ITaskItem item in Files)
            {
                string path = item.GetMetadata("FullPath");
                if (!File.Exists(path))
                {
                    continue;
                }

                // File.Create truncates to zero bytes; the immediate Dispose
                // closes the handle without writing anything.
                using (File.Create(path)) { }
                count++;
            }

            Log.LogMessage(MessageImportance.High, "Stripped " + count + " markup file(s) to zero bytes");
            return true;
        }
    }
}
