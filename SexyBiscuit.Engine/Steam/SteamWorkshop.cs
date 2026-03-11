#if STEAMWORKS
using Steamworks;
#endif

namespace SexyBiscuit.Engine.Steam;

/// <summary>
/// Static helpers for Steam Workshop: create items, submit updates, enumerate subscriptions,
/// and look up install paths.
/// </summary>
public static class SteamWorkshop
{
    // -------------------------------------------------------------------------
    // Events
    // -------------------------------------------------------------------------

    /// <summary>Fired when a new Workshop item has been created. (success, publishedFileId)</summary>
    public static event Action<bool, ulong>? OnItemCreated;

    /// <summary>Fired when an item update has been submitted. (success)</summary>
    public static event Action<bool>? OnItemSubmitted;

    /// <summary>Fired when subscribed-item paths have been collected. (list of install paths)</summary>
    public static event Action<List<string>>? OnSubscribedItemsLoaded;

#if STEAMWORKS
    // CallResult handles must live as fields to prevent GC collection mid-flight
    private static CallResult<CreateItemResult_t>?       _createResult;
    private static CallResult<SubmitItemUpdateResult_t>? _submitResult;
#endif

    // -------------------------------------------------------------------------
    // Create item
    // -------------------------------------------------------------------------

#if STEAMWORKS
    /// <summary>Requests Steam to create a new Workshop item of the given type.</summary>
    public static void CreateItem(uint appId,
        EWorkshopFileType type = EWorkshopFileType.k_EWorkshopFileTypeCommunity)
    {
        if (SteamManager.Instance?.IsInitialised != true) return;

        var call = SteamUGC.CreateItem(new AppId_t(appId), type);
        _createResult = CallResult<CreateItemResult_t>.Create(
            (result, ioFail) =>
            {
                bool success = !ioFail && result.m_eResult == EResult.k_EResultOK;
                OnItemCreated?.Invoke(success, (ulong)result.m_nPublishedFileId);
            });
        _createResult.Set(call);
    }
#else
    public static void CreateItem(uint appId) { }
#endif

    // -------------------------------------------------------------------------
    // Submit update
    // -------------------------------------------------------------------------

#if STEAMWORKS
    /// <summary>
    /// Starts a Workshop item update, sets all metadata fields, and submits.
    /// The item must already exist (use CreateItem first).
    /// </summary>
    public static void SubmitUpdate(
        ulong  publishedFileId,
        string title,
        string description,
        string contentPath,
        string previewImagePath,
        string[] tags)
    {
        if (SteamManager.Instance?.IsInitialised != true) return;

        var fileId = new PublishedFileId_t(publishedFileId);
        var handle = SteamUGC.StartItemUpdate(SteamUtils.GetAppID(), fileId);

        if (!string.IsNullOrEmpty(title))
            SteamUGC.SetItemTitle(handle, title);

        if (!string.IsNullOrEmpty(description))
            SteamUGC.SetItemDescription(handle, description);

        if (!string.IsNullOrEmpty(contentPath))
            SteamUGC.SetItemContent(handle, contentPath);

        if (!string.IsNullOrEmpty(previewImagePath))
            SteamUGC.SetItemPreview(handle, previewImagePath);

        if (tags != null && tags.Length > 0)
            SteamUGC.SetItemTags(handle, tags.ToList());

        var call = SteamUGC.SubmitItemUpdate(handle, null);
        _submitResult = CallResult<SubmitItemUpdateResult_t>.Create(
            (result, ioFail) =>
            {
                bool success = !ioFail && result.m_eResult == EResult.k_EResultOK;
                OnItemSubmitted?.Invoke(success);
            });
        _submitResult.Set(call);
    }
#else
    public static void SubmitUpdate(ulong publishedFileId, string title, string description,
        string contentPath, string previewImagePath, string[] tags) { }
#endif

    // -------------------------------------------------------------------------
    // Subscribed items
    // -------------------------------------------------------------------------

    /// <summary>
    /// Synchronously enumerates all subscribed Workshop items and fires
    /// <see cref="OnSubscribedItemsLoaded"/> with a list of their local install paths.
    /// Items that are not yet installed on disk are skipped.
    /// </summary>
    public static void GetSubscribedItems()
    {
#if STEAMWORKS
        if (SteamManager.Instance?.IsInitialised != true) return;

        uint count = SteamUGC.GetNumSubscribedItems();
        if (count == 0) { OnSubscribedItemsLoaded?.Invoke(new List<string>()); return; }

        var fileIds = new PublishedFileId_t[count];
        SteamUGC.GetSubscribedItems(fileIds, count);

        var paths = new List<string>((int)count);
        foreach (var id in fileIds)
        {
            string path = GetItemInstallPath(id);
            if (!string.IsNullOrEmpty(path))
                paths.Add(path);
        }

        OnSubscribedItemsLoaded?.Invoke(paths);
#else
        OnSubscribedItemsLoaded?.Invoke(new List<string>());
#endif
    }

    // -------------------------------------------------------------------------
    // Install path query
    // -------------------------------------------------------------------------

#if STEAMWORKS
    /// <summary>Returns the local install path for a subscribed Workshop item, or empty string.</summary>
    public static string GetItemInstallPath(PublishedFileId_t id)
    {
        if (SteamManager.Instance?.IsInitialised != true) return string.Empty;

        bool installed = SteamUGC.GetItemInstallInfo(
            id,
            out ulong _sizeOnDisk,
            out string folder,
            1024,
            out uint _timeStamp);

        return installed ? folder : string.Empty;
    }

    /// <summary>Overload accepting a raw ulong file ID.</summary>
    public static string GetItemInstallPath(ulong publishedFileId)
        => GetItemInstallPath(new PublishedFileId_t(publishedFileId));
#else
    public static string GetItemInstallPath(ulong publishedFileId) => string.Empty;
#endif
}
