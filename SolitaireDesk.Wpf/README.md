# Solitaire Desk — WPF Design Handoff

**Read this first.** This project is **not a new app to build from scratch** — it is a **design
package** for the Diamond Sales & Inventory WPF app that is **already under development**.

The functional project already exists and is ongoing; only its **UI / design is not up to the mark**.
This package contains the **design layer** — a complete, themeable WPF design system plus every
screen laid out to spec. The job is to **lift the design (theme + styles + layouts) out of these
files and apply it inside the existing project**, then keep building on the current codebase.

There is **no business logic here** — it is pure UI/XAML. Nothing about your existing view models,
services, or data flow needs to change; you are reskinning, not rewriting.

---

## What to take from here (the design)

| Take this | It gives you |
|---|---|
| **`Themes/LightTheme.xaml`**, **`Themes/DarkTheme.xaml`** | The full color palette as brushes — identical keys in both themes, so light/dark is a one-dictionary swap. |
| **`Themes/Typography.xaml`** | Fonts, type scale, corner radii, spacing tokens. |
| **`Styles/Buttons.xaml`** | Primary / Secondary / Ghost / Danger / Icon / RowIcon button styles. |
| **`Styles/Inputs.xaml`** | TextBox, Numeric, ComboBox, ToggleSwitch, CheckBox, and in-grid cell editors. |
| **`Styles/Widgets.xaml`** | Card, Chip (status tags), NavButton, TabButton, SegmentButton, CountPill, Divider. |
| **`Styles/DataGridStyles.xaml`** | DataGrid + header / row / cell styling and numeric/mono/muted cell text. |
| **`Views/*.xaml`** | **Reference layouts** for each screen — the visual spec to reproduce in your existing views. |

**Ignore for production:** `Design/SampleData.cs` and the `x:Static` bindings in the views exist only
so the screens render while previewing the design. Do not carry them into the real project.

---

## How to apply this design in the existing project

### 1. Copy the design system in
Copy these two folders into the existing solution (keep the folder names or adjust namespaces):

```
Themes/   ->  LightTheme.xaml, DarkTheme.xaml, Typography.xaml
Styles/   ->  Buttons.xaml, Inputs.xaml, Widgets.xaml, DataGridStyles.xaml
Services/ ->  ThemeManager.cs   (optional — only if you want the runtime light/dark swap)
```

### 2. Merge the dictionaries in the existing `App.xaml`
Add these to the app's `MergedDictionaries` **in this order** (theme first, then tokens, then styles):

```xml
<ResourceDictionary.MergedDictionaries>
    <ResourceDictionary Source="Themes/LightTheme.xaml"/>   <!-- swapped at runtime for Dark -->
    <ResourceDictionary Source="Themes/Typography.xaml"/>
    <ResourceDictionary Source="Styles/Buttons.xaml"/>
    <ResourceDictionary Source="Styles/Inputs.xaml"/>
    <ResourceDictionary Source="Styles/Widgets.xaml"/>
    <ResourceDictionary Source="Styles/DataGridStyles.xaml"/>
    <!-- ...the existing project's own dictionaries can follow -->
</ResourceDictionary.MergedDictionaries>
```

Fix the `Source` paths / `clr-namespace` to match wherever the files land in your project.

### 3. Apply the styles to the existing controls
The existing screens keep their view models and bindings — just point each control at the design
style. Examples:

```xml
<Button Style="{StaticResource PrimaryButton}"   Content="Post invoice"/>
<Button Style="{StaticResource SecondaryButton}" Content="Save draft"/>
<TextBox  Style="{StaticResource FieldTextBox}"/>
<TextBox  Style="{StaticResource NumericTextBox}"/>
<ComboBox Style="{StaticResource FieldComboBox}" ItemContainerStyle="{StaticResource FieldComboBoxItem}"/>
<Border Style="{StaticResource Card}" Padding="16"> … </Border>
<Label  Style="{StaticResource Chip}" Tag="Posted" Content="POSTED"/>   <!-- Tags: Posted/Draft/Cancelled/Overdue/Due -->

<DataGrid Style="{StaticResource DataGridStyle}" ItemsSource="{Binding Invoices}">
  <DataGrid.Columns>
    <DataGridTextColumn Header="Buyer"  Binding="{Binding Buyer}"  Width="*"
                        ElementStyle="{StaticResource CellText}"/>
    <DataGridTextColumn Header="Amount" Binding="{Binding Amount}" Width="132"
                        ElementStyle="{StaticResource NumericCellText}"
                        HeaderStyle="{StaticResource DataGridNumericHeaderStyle}"/>
  </DataGrid.Columns>
</DataGrid>
```

### 4. Match each screen to its reference layout
For each screen in the existing app, open the matching file here and reproduce the layout
(structure, spacing, which style goes where), then keep the existing bindings:

| Existing screen | Reference layout in this package |
|---|---|
| Login | `Views/LoginView.xaml` |
| Dashboard / home | `Views/DashboardView.xaml` |
| Sales / invoice list | `Views/SalesListView.xaml` |
| New / edit invoice | `Views/SalesEntryView.xaml`  ← the detailed one |
| Stock / inventory | `Views/InventoryView.xaml` |
| Outstanding / receivables | `Views/ReceivablesView.xaml` |
| Masters (grades, buyers…) | `Views/MastersView.xaml` |
| Settings | `Views/SettingsView.xaml` |
| Dialogs / toasts | `Views/DialogsPreview.xaml` |
| App shell (nav + top bar) | `MainWindow.xaml` |

Two ways to use the views: **(a)** copy the XAML layout into the existing view and swap the
`{x:Static SampleData…}` for the real `{Binding …}`; or **(b)** treat them as a visual reference and
restyle the existing views to match. Either way the design comes from here; the logic stays in the
existing project.

### 5. (Optional) runtime light/dark
If the existing app wants the theme toggle, call `ThemeManager.Apply(...)` / `ThemeManager.Toggle()`
(adjust its dictionary source paths to your project). If not needed, drop `ThemeManager.cs` and just
keep whichever theme dictionary you merged.

---

## Previewing the design as-is (optional)

This folder is also a self-contained, runnable preview so you can *see* the intended design before
integrating. **Windows + .NET 8 SDK required** (WPF is Windows-only):

```powershell
cd SolitaireDesk.Wpf
dotnet run
```

Opens on the Dashboard; the left nav switches screens; the ◐ button in the top bar flips light/dark.
This preview is a reference only — the real work happens in the existing project.

---

## Design tokens (reference)

Every color is a keyed brush defined **identically** in `LightTheme.xaml` and `DarkTheme.xaml`.
Always reference them with **`DynamicResource`** (never a hard-coded hex) so theme-switching keeps
working.

| Token | Meaning |
|---|---|
| `BgBrush` / `SurfaceBrush` / `Surface2Brush` | page ground / cards / raised (inputs, headers) |
| `BorderBrush` | hairlines |
| `TextBrush` / `TextMutedBrush` | primary / secondary text |
| `AccentBrush` / `AccentHoverBrush` / `AccentSoftBrush` | sapphire accent, hover, tinted fill |
| `SuccessBrush` / `WarningBrush` / `DangerBrush` | semantic state (kept separate from the accent) |
| `*SoftBrush` / `*BorderBrush` | chip / state fills |
| `CardShadow` / `PopupShadow` | elevation effects |

Type styles: `DisplayText`, `H1Text`–`H3Text`, `BodyText`, `MutedText`, `LabelCaps` (uppercase
micro-label), `MonoText` / `MonoDisplay` (tabular figures). Radii: `RadiusCard`=8, `RadiusControl`=6,
`RadiusPill`.

---

## Notes for whoever integrates this

- Reference brushes with `DynamicResource`, fonts/radii with `StaticResource` — same as the files here.
- **`DatePicker` and `PasswordBox`** use WPF's default chrome (only lightly tinted). If the existing
  app needs them fully on-token, add templated styles following the `FieldTextBox` / `FieldComboBox`
  pattern in `Inputs.xaml`.
- **Chips** show text as-authored — pass chip `Content` in UPPERCASE (WPF has no CSS `text-transform`).
- The HTML's motion/sparklines are represented statically; add `Storyboard`s (150–200 ms) on
  hover/selection if you want the animation.
- Inventory heat-cell backgrounds use fixed accent alpha for the mock; compute the alpha from the
  real cell value when binding data.
- All 18 XAML files are well-formed and self-consistent; this was authored on macOS, so do a first
  build on Windows and let the existing project's namespaces/paths settle in.
```
