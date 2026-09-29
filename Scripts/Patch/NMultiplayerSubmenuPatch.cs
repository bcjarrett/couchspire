using HarmonyLib;
using CouchSpire.Scripts.Runtime;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Saves;

namespace CouchSpire.Scripts.Patch;

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
        ModLog.Info($"Detected a local co-op save marker; attempting to continue the game: {string.Join(",", playerIds)}");
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

            ModLog.Warn("Failed to read the local co-op save; showed the corrupted-save popup.");
            return false;
        }

        if (!LocalSelfCoopContext.IsSaveOwnedByLocalSelfCoop(readSaveResult.SaveData))
        {
            ModLog.Warn("Detected that save player IDs don't match the local co-op marker; falling back to the native multiplayer load flow.");
            LocalSelfCoopSaveTag.ClearCurrentProfile();
            return true;
        }

        NSubmenuStack? stack = AccessTools.Field(typeof(NSubmenu), "_stack")?.GetValue(__instance) as NSubmenuStack;
        if (stack == null)
        {
            ModLog.Warn("Submenu stack not found; falling back to the native multiplayer load flow.");
            return true;
        }

        LocalLoopbackHostGameService netService = new LocalLoopbackHostGameService(primaryPlayerId);
        LocalSelfCoopContext.Enable(netService);
        NMultiplayerLoadGameScreen loadGameScreen = stack.GetSubmenuType<NMultiplayerLoadGameScreen>();
        loadGameScreen.InitializeAsHost(netService, readSaveResult.SaveData);
        stack.Push(loadGameScreen);
        ModLog.Info("Opened the multiplayer load screen using the local loopback service.");
        return false;
    }
}
