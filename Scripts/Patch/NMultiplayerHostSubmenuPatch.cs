using Godot;
using HarmonyLib;
using LocalMultiControl.Scripts.Runtime;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Saves;

namespace LocalMultiControl.Scripts.Patch;

[HarmonyPatch(typeof(NMultiplayerHostSubmenu), nameof(NMultiplayerHostSubmenu._Ready))]
internal static class NMultiplayerHostSubmenuPatch
{
    private const string LocalSelfCoopButtonName = "LocalSelfCoopButton";
    private const float CardGap = 26f;

    [HarmonyPostfix]
    private static void Postfix(NMultiplayerHostSubmenu __instance)
    {
        try
        {
            if (__instance.GetNodeOrNull<NSubmenuButton>(LocalSelfCoopButtonName) != null)
            {
                LocalMultiControlLogger.Info("Multiplayer menu entry already exists; skipping duplicate injection.");
                return;
            }

            NSubmenuButton? standardButton = __instance.GetNodeOrNull<NSubmenuButton>("StandardButton");
            NSubmenuButton? dailyButton = __instance.GetNodeOrNull<NSubmenuButton>("DailyButton");
            NSubmenuButton? customButton = __instance.GetNodeOrNull<NSubmenuButton>("CustomRunButton");
            if (standardButton == null)
            {
                LocalMultiControlLogger.Warn("StandardButton not found; cannot inject the local multi-character entry.");
                return;
            }

            NSubmenuButton templateButton = customButton ?? standardButton;
            NSubmenuButton button = CreateStyledButton(templateButton);
            button.Name = LocalSelfCoopButtonName;
            EnsureVisualResourcesUnique(button);
            ApplyButtonText(button);
            button.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>((_) => OnLocalSelfCoopPressed(__instance)));

            Control container = templateButton.GetParent<Control>();
            container.AddChild(button);
            int targetIndex = Math.Min(templateButton.GetIndex() + 1, container.GetChildCount() - 1);
            container.MoveChild(button, targetIndex);

            ArrangeFourButtonsHorizontally(standardButton, dailyButton, customButton, button);
            LocalMultiControlLogger.Info("Multiplayer menu injected a card-style entry: single-player multi-character (four cards side by side).");
        }
        catch (Exception exception)
        {
            LocalMultiControlLogger.Error($"Failed to inject the \"single-player multi-character\" entry: {exception}");
        }
    }

    private static NSubmenuButton CreateStyledButton(NSubmenuButton templateButton)
    {
        const Node.DuplicateFlags duplicateFlags = Node.DuplicateFlags.Groups |
                                                   Node.DuplicateFlags.Scripts |
                                                   Node.DuplicateFlags.UseInstantiation;
        return templateButton.Duplicate((int)duplicateFlags) as NSubmenuButton
            ?? throw new InvalidOperationException("Failed to duplicate the template button.");
    }

    private static void EnsureVisualResourcesUnique(NSubmenuButton button)
    {
        Node? bgPanelNode = button.FindChild("BgPanel", recursive: true, owned: false);
        if (bgPanelNode is CanvasItem bgPanel && bgPanel.Material is ShaderMaterial material)
        {
            bgPanel.Material = material.Duplicate() as ShaderMaterial;
        }
    }

    private static void ApplyButtonText(NSubmenuButton button)
    {
        Node? titleNode = button.FindChild("Title", recursive: true, owned: false);
        if (titleNode is Label title)
        {
            title.Text = LocalModText.LocalSelfCoopCardTitle;
        }

        Node? descriptionNode = button.FindChild("Description", recursive: true, owned: false);
        if (descriptionNode is RichTextLabel description)
        {
            description.Text = LocalModText.LocalSelfCoopCardDescription;
        }
    }

    private static void ArrangeFourButtonsHorizontally(
        NSubmenuButton standardButton,
        NSubmenuButton? dailyButton,
        NSubmenuButton? customButton,
        NSubmenuButton localButton)
    {
        if (dailyButton == null || customButton == null)
        {
            return;
        }

        List<NSubmenuButton> originalButtons = new() { standardButton, dailyButton, customButton };
        originalButtons = originalButtons.OrderBy((item) => item.Position.X).ToList();

        float y = originalButtons[0].Position.Y;
        float cardWidth = originalButtons[0].Size.X;
        if (cardWidth <= 1f)
        {
            cardWidth = localButton.Size.X;
        }

        if (cardWidth <= 1f)
        {
            return;
        }

        float centerX = originalButtons.Average((item) => item.Position.X + item.Size.X * 0.5f);
        float totalWidth = cardWidth * 4f + CardGap * 3f;
        float startX = centerX - totalWidth * 0.5f;
        List<NSubmenuButton> arranged = new() { localButton, originalButtons[0], originalButtons[1], originalButtons[2] };
        for (int index = 0; index < arranged.Count; index++)
        {
            NSubmenuButton button = arranged[index];
            float x = startX + index * (cardWidth + CardGap);
            button.Position = new Vector2(x, y);
        }
    }

    private static void OnLocalSelfCoopPressed(NMultiplayerHostSubmenu submenu)
    {
        LocalMultiControlLogger.Info("Entering the single-player multi-character flow.");
        LocalSelfCoopSaveTag.ClearCurrentProfile();
        SaveManager.Instance.DeleteCurrentMultiplayerRun();
        LocalMultiControlLogger.Info("Cleared historical multiplayer saves to avoid interference from old-format validation.");

        NSubmenuStack? stack = GetStack(submenu);
        if (stack == null)
        {
            LocalMultiControlLogger.Warn("Cannot open character select: NSubmenuStack not found.");
            return;
        }

        ulong primaryPlayerId = LocalSelfCoopContext.ResolvePrimaryPlayerId();
        LocalSelfCoopSaveTag.MarkCurrentProfile(LocalSelfCoopContext.LocalPlayerIds);
        LocalLoopbackHostGameService netService = new LocalLoopbackHostGameService(primaryPlayerId);
        LocalSelfCoopContext.Enable(netService);

        NCharacterSelectScreen characterSelectScreen = stack.GetSubmenuType<NCharacterSelectScreen>();
        LocalSelfCoopContext.ActiveCharacterSelectScreen = characterSelectScreen;
        characterSelectScreen.InitializeMultiplayerAsHost(netService, LocalSelfCoopContext.PlayerCount);
        if (!LocalSelfCoopContext.BootstrapLocalPlayers(characterSelectScreen))
        {
            LocalMultiControlLogger.Warn("Local co-op lobby setup failed.");
        }

        stack.Push(characterSelectScreen);
        NGame.Instance?.AddChildSafely(NFullscreenTextVfx.Create(LocalModText.EnteredLocalSelfCoopHint));
        LocalMultiControlLogger.Info("Navigated to the local multi-character team's character select screen.");
    }

    private static NSubmenuStack? GetStack(NSubmenu submenu)
    {
        return AccessTools.Field(typeof(NSubmenu), "_stack").GetValue(submenu) as NSubmenuStack;
    }
}
