using System;
using Microsoft.Extensions.Logging;
using Trebuchet.Assets;
using TrebuchetLib;
using TrebuchetLib.Services;

namespace Trebuchet.ViewModels;

/// <summary>
/// Keeps the server instance selections (and the game client selection) valid when saves or mod lists are renamed or
/// deleted: an instance pointing to a missing name silently started with the first save/mod list instead, which can
/// load a world with the wrong mods.
/// </summary>
public static class ProfileReferences
{
    public static void KeepConfigInSync<T, TRef>(this FileMenuViewModel<T, TRef> menu, AppSetup setup, ILogger logger)
        where T : ProfileFile<T>
        where TRef : class, IPRef<T, TRef>
    {
        menu.DeleteBlocker = reference =>
        {
            var instances = setup.Config.GetInstancesUsing(reference.Uri.OriginalString);
            return instances.Count == 0
                ? null
                : string.Format(Resources.ProfileInUseByInstances, string.Join(", ", instances));
        };
        menu.FileRenamed += (oldReference, newReference) =>
        {
            if (!setup.Config.ReplaceReference(oldReference.Uri.OriginalString, newReference.Uri.OriginalString))
                return;
            try
            {
                setup.Config.SaveFile();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, @"Could not save the renamed selection {Name}", newReference);
            }
        };
    }
}
