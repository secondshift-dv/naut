using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Neuterradise.App.Settings;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.Ui;

internal sealed class VaultSettingsPanel
{
    private readonly AppServices _services;
    private readonly SettingsViewModel _vm;
    private readonly ExplorerLocationProvider _explorer;

    public VaultSettingsPanel(AppServices services, SettingsViewModel vm)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        _explorer = new ExplorerLocationProvider(services.Paths);
    }

public FrameworkElement Build()
    {
        var hasVault = Path.IsPathFullyQualified(_vm.VaultRoot);
        var vaultRoot = hasVault
            ? _vm.VaultRoot
            : UI.T("Settings.Vault.NoVault", "No Vault is open.");
        var currentName = hasVault
            ? Path.GetFileName(vaultRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : string.Empty;

        var renameStatus = UI.WrappedText(string.Empty, "caption", "textSecondary");
        renameStatus.Visibility = Visibility.Collapsed;
        var nameWarning = UI.WrappedText(string.Empty, "caption", "danger");
        nameWarning.Visibility = Visibility.Collapsed;
        var nameRules = UI.WrappedText(
            UI.F(
                "Settings.Vault.NameRules",
                "Folder-name rules: do not use {0}; no control characters, trailing period/space, or Windows device names such as CON, PRN, AUX, NUL, COM1, or LPT1.",
                VaultNameRules.ForbiddenCharactersDisplay),
            "caption",
            "textSecondary");

        var nameInput = UI.Input(
            UI.T("Settings.Vault.VaultNamePlaceholder", "Vault name"),
            currentName);
        nameInput.Width = 220;
        nameInput.MaxWidth = 280;
        nameInput.IsEnabled = hasVault;

        Button? renameButton = null;

        string ValidationText(VaultNameViolation violation) => violation switch
        {
            VaultNameViolation.EmptyOrTooLong => UI.F(
                "Settings.Vault.NameError.Length",
                "Use 1 to {0} characters.",
                VaultNameRules.MaxLength),
            VaultNameViolation.SurroundingWhitespace => UI.T(
                "Settings.Vault.NameError.Space",
                "The Vault name cannot begin or end with a space."),
            VaultNameViolation.DirectorySeparator => UI.T(
                "Settings.Vault.NameError.Separator",
                @"Do not use \ or /. They are path/directory separators, not Vault-name characters."),
            VaultNameViolation.InvalidCharacter => UI.F(
                "Settings.Vault.NameError.Character",
                "This name contains a Windows-reserved filename character. Do not use {0} or control characters.",
                VaultNameRules.ForbiddenCharactersDisplay),
            VaultNameViolation.TrailingPeriod => UI.T(
                "Settings.Vault.NameError.Period",
                "The Vault name cannot end with a period."),
            VaultNameViolation.ReservedDeviceName => UI.T(
                "Settings.Vault.NameError.Reserved",
                "That name is reserved by Windows (for example CON, PRN, AUX, NUL, COM1, or LPT1)."),
            _ => UI.T(
                "Settings.Vault.RenameInvalid",
                "That Vault name cannot be used."),
        };

        void UpdateRenameValidation()
        {
            var violation = VaultNameRules.Validate(nameInput.Text, out var normalized);
            if (violation != VaultNameViolation.None)
            {
                nameWarning.Text = ValidationText(violation);
                nameWarning.Visibility = Visibility.Visible;
                if (renameButton is not null)
                {
                    renameButton.IsEnabled = false;
                }

                return;
            }

            nameWarning.Text = string.Empty;
            nameWarning.Visibility = Visibility.Collapsed;
            if (renameButton is not null)
            {
                renameButton.IsEnabled =
                    hasVault
                    && !string.Equals(currentName, normalized, StringComparison.OrdinalIgnoreCase);
            }
        }

        async Task RenameAsync()
        {
            if (!hasVault)
            {
                return;
            }

            var violation = VaultNameRules.Validate(nameInput.Text, out var normalized);
            if (violation != VaultNameViolation.None)
            {
                nameWarning.Text = ValidationText(violation);
                nameWarning.Visibility = Visibility.Visible;
                UpdateRenameValidation();
                return;
            }

            if (string.Equals(currentName, normalized, StringComparison.OrdinalIgnoreCase))
            {
                renameStatus.Text = UI.T("Settings.Vault.RenameUnchanged", "Choose a different Vault name.");
                renameStatus.Visibility = Visibility.Visible;
                return;
            }

            var parent = Path.GetDirectoryName(vaultRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var target = string.IsNullOrWhiteSpace(parent) ? normalized : Path.Combine(parent, normalized);
            var confirmed = await _services.ConfirmAsync(
                UI.T("Settings.Vault.RenameTitle", "Rename Vault and restart?"),
                UI.F(
                    "Settings.Vault.RenameConfirm",
                    "naut will close the current Vault safely, rename its folder to {0}, update the saved Vault path, and restart.\n\n{1}",
                    normalized,
                    target),
                UI.T("Settings.Vault.RenameAction", "Rename & restart"))
                .ConfigureAwait(true);
            if (!confirmed)
            {
                return;
            }

            nameInput.IsEnabled = false;
            if (renameButton is not null)
            {
                renameButton.IsEnabled = false;
            }

            renameStatus.Text = UI.T("Settings.Vault.Renaming", "Preparing Vault rename…");
            renameStatus.Visibility = Visibility.Visible;
            var result = await _services.RequestVaultRenameAsync(normalized).ConfigureAwait(true);
            if (!result.Succeeded)
            {
                nameInput.IsEnabled = true;
                renameStatus.Text = result.SafeErrorDetail
                    ?? UI.T("Settings.Vault.RenameFailed", "The Vault could not be renamed.");
                renameStatus.Visibility = Visibility.Visible;
                UpdateRenameValidation();
            }
        }

        renameButton = UI.Button(
            UI.T("Settings.Vault.RenameAction", "Rename & restart"),
            () => _services.RunUserAction(
                RenameAsync(),
                "Settings.RenameVault",
                UI.T("Settings.Vault.RenameFailed", "The Vault could not be renamed.")),
            ButtonKind.Primary);
        nameInput.TextChanged += (_, _) =>
        {
            renameStatus.Visibility = Visibility.Collapsed;
            UpdateRenameValidation();
        };
        UpdateRenameValidation();

        var moveStatus = UI.WrappedText(string.Empty, "caption", "textSecondary");
        moveStatus.Visibility = Visibility.Collapsed;

        async Task MoveAsync()
        {
            if (!hasVault)
            {
                return;
            }

            var targetParent = await _services.PickFolderAsync().ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(targetParent))
            {
                return;
            }

            var targetRoot = Path.Combine(targetParent, currentName);
            if (RootPathRules.AreSameRoot(vaultRoot, targetRoot))
            {
                moveStatus.Text = UI.T(
                    "Settings.Vault.MoveUnchanged",
                    "Choose a different destination folder.");
                moveStatus.Visibility = Visibility.Visible;
                return;
            }

            var confirmed = await _services.ConfirmAsync(
                UI.T("Settings.Vault.MoveTitle", "Move Vault and restart?"),
                UI.F(
                    "Settings.Vault.MoveConfirm",
                    "naut will move this entire Vault to the selected location and restart. The Vault keeps the same Vault ID and all of its data.\n\n{0}",
                    targetRoot),
                UI.T("Settings.Vault.MoveAction", "Move & restart"))
                .ConfigureAwait(true);
            if (!confirmed)
            {
                return;
            }

            moveStatus.Text = UI.T(
                "Settings.Vault.Moving",
                "Preparing Vault move…");
            moveStatus.Visibility = Visibility.Visible;
            var result = await _services
                .RequestVaultMoveAsync(targetParent)
                .ConfigureAwait(true);
            if (!result.Succeeded)
            {
                moveStatus.Text = result.SafeErrorDetail
                    ?? UI.T("Settings.Vault.MoveFailed", "The Vault could not be moved.");
                moveStatus.Visibility = Visibility.Visible;
            }
        }

        var moveButton = UI.Button(
            UI.T("Settings.Vault.MoveAction", "Move & restart"),
            () => _services.RunUserAction(
                MoveAsync(),
                "Settings.MoveVault",
                UI.T("Settings.Vault.MoveFailed", "The Vault could not be moved.")),
            ButtonKind.Secondary);
        moveButton.IsEnabled = hasVault;
        var changeStatus = UI.WrappedText(string.Empty, "caption", "textSecondary");
        changeStatus.Visibility = Visibility.Collapsed;

        async Task ChangeAsync()
        {
            if (!hasVault)
            {
                return;
            }

            var selectedRoot = await _services.PickFolderAsync().ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(selectedRoot))
            {
                return;
            }

            if (RootPathRules.AreSameRoot(vaultRoot, selectedRoot))
            {
                changeStatus.Text = UI.T(
                    "Settings.Vault.ChangeUnchanged",
                    "That folder is already the current Vault.");
                changeStatus.Visibility = Visibility.Visible;
                return;
            }

            var confirmed = await _services.ConfirmAsync(
                UI.T("Settings.Vault.ChangeTitle", "Change Vault and restart?"),
                UI.F(
                    "Settings.Vault.ChangeConfirm",
                    "naut will switch to the selected folder without moving the current Vault. Select an existing naut Vault, or an empty folder that you created in the Windows picker for a new Vault. The selected folder itself is never created by the app.\n\n{0}",
                    selectedRoot),
                UI.T("Settings.Vault.ChangeAction", "Change & restart"))
                .ConfigureAwait(true);
            if (!confirmed)
            {
                return;
            }

            changeStatus.Text = UI.T(
                "Settings.Vault.Changing",
                "Preparing Vault change…");
            changeStatus.Visibility = Visibility.Visible;
            var result = await _services
                .RequestVaultChangeAsync(selectedRoot)
                .ConfigureAwait(true);
            if (!result.Succeeded)
            {
                changeStatus.Text = result.SafeErrorDetail
                    ?? UI.T("Settings.Vault.ChangeFailed", "The selected folder could not be used as a Vault.");
                changeStatus.Visibility = Visibility.Visible;
            }
        }

        var changeButton = UI.Button(
            UI.T("Settings.Vault.ChangeAction", "Change & restart"),
            () => _services.RunUserAction(
                ChangeAsync(),
                "Settings.ChangeVault",
                UI.T("Settings.Vault.ChangeFailed", "The selected folder could not be used as a Vault.")),
            ButtonKind.Secondary);
        changeButton.IsEnabled = hasVault;
        var identity = UI.Section(
            UI.T("Settings.Vault.IdentityTitle", "Vault identity"),
            UI.T("Settings.Vault.IdentityDesc", "The folder name is the Vault name shown in the app. Renaming safely restarts naut."),
            UI.Wrap(
                6,
                new IconView("icon.vault.identity", 16, "accent"),
                nameInput,
                renameButton),
            nameRules,
            nameWarning,
            renameStatus);

        return UI.V(
            12,
            identity,
            UI.Section(
                UI.T("Settings.Vault.StorageTitle", "Vault storage"),
                null,
                UI.Text(vaultRoot, "mono", maxLines: 3),
                UI.Text(
                    UI.F("Settings.Vault.Counts", "{0} Profiles · {1} media · {2}", _vm.TotalProfilesCount, _vm.ActiveMediaCount, _vm.ManagedStorageFormatted),
                    "body"),
                UI.Wrap(
                    6,
                    changeButton,
                    moveButton,
                    UI.Button(
                        UI.T("Settings.Vault.OpenVault", "Open Vault"),
                        () => _services.RunUserAction(OpenVaultAsync(), "Settings.OpenVault", UI.T("Settings.Vault.OpenVaultFailed", "The Vault could not be opened.")),
                        ButtonKind.Secondary)),
                UI.Text(
                    UI.T(
                        "Settings.Vault.ChangeHint",
                        "Change switches to another Vault. Move relocates this entire Vault to a folder you choose and keeps the same Vault ID."),
                    "caption",
                    "textSecondary",
                    4),
                changeStatus,
                moveStatus));
    }
    private async Task OpenVaultAsync()
    {
        var result = await _explorer.OpenVaultAsync().ConfigureAwait(true);
        if (result.Status != StorageOperationStatus.Success && result.Status != StorageOperationStatus.Cancelled)
        {
            throw new InvalidOperationException(result.SafeErrorDetail ?? "The Vault could not be opened.");
        }
    }

}
