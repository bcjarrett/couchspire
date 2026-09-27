using HarmonyLib;
using LocalMultiControl.Scripts.Runtime;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Saves;

namespace LocalMultiControl.Scripts.Patch;

[HarmonyPatch(typeof(NMultiplayerSubmenu), "StartLoad")]
internal static class NMultiplayerSubmenuPatch
{
    [HarmonyPrefix]
    private static bool Prefix(NMultiplayerSubmenu __instance)
    {
        if (!LocalSelfCoopSaveTag.TryReadCurrentProfile(out List<ulong> playerIds) || playerIds.Count < 2)
        {
            return true;
        }

        ulong primaryPlayerId = playerIds[0];
        LocalMultiControlLogger.Info($"Detected a local multi-control save marker; attempting to continue the game: {string.Join(",", playerIds)}");
        LocalSelfCoopContext.UseSavedPlayerIds(playerIds);

        ReadSaveResult<SerializableRun> readSaveResult = SaveManager.Instance.LoadAndCanonicalizeMultiplayerRunSave(primaryPlayerId);
        if (!readSaveResult.Success || readSaveResult.SaveData == null)
        {
            NSubmenuButton? loadButton = AccessTools.Field(typeof(NMultiplayerSubmenu), "_loadButton")?.GetValue(__instance) as NSubmenuButton;
            loadButton?.Disable();
            NErrorPopup? popup = NErrorPopup.Create(
                new LocString("main_menu_ui", "INVALID_SAVE_POPUP.title"),
                new LocString("main_menu_ui", "INVALID_SAVE_POPUP.description_run"),
                new LocString("main_menu_ui", "INVALID_SAVE_POPUP.dismiss"),
                showReportBugButton: true);

            if (popup != null && NModalContainer.Instance != null)
            {
                NModalContainer.Instance.Add(popup);
                NModalContainer.Instance.ShowBackstop();
            }

            LocalMultiControlLogger.Warn("Failed to read the local multi-control save; showed the corrupted-save popup.");
            return false;
        }

        if (!LocalSelfCoopContext.IsSaveOwnedByLocalSelfCoop(readSaveResult.SaveData))
        {
            LocalMultiControlLogger.Warn("Detected that save player IDs don't match the local multi-control marker; falling back to the native multiplayer load flow.");
            LocalSelfCoopSaveTag.ClearCurrentProfile();
            return true;
        }

        NSubmenuStack? stack = AccessTools.Field(typeof(NSubmenu), "_stack")?.GetValue(__instance) as NSubmenuStack;
        if (stack == null)
        {
            LocalMultiControlLogger.Warn("Submenu stack not found; falling back to the native multiplayer load flow.");
            return true;
        }

        LocalLoopbackHostGameService netService = new LocalLoopbackHostGameService(primaryPlayerId);
        LocalSelfCoopContext.Enable(netService);
        NMultiplayerLoadGameScreen loadGameScreen = stack.GetSubmenuType<NMultiplayerLoadGameScreen>();
        loadGameScreen.InitializeAsHost(netService, readSaveResult.SaveData);
        stack.Push(loadGameScreen);
        LocalMultiControlLogger.Info("Opened the multiplayer load screen using the local loopback service.");
        return false;
    }
}
