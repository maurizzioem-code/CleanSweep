# ================================================================ THEME: Windows 11 style dark UI
# Dark palette, rounded cards, sidebar navigation, native dark title bar / scrollbars / headers.
# Purely visual - loaded first (palette + helpers), applied last (Initialize-Shell).

if (-not ([System.Management.Automation.PSTypeName]'CSNative').Type) {
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class CSNative {
  [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
  [DllImport("uxtheme.dll", CharSet=CharSet.Unicode)] public static extern int SetWindowTheme(IntPtr h, string app, string id);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
  // Undocumented but stable since Windows 10 1809: lets standard controls (scrollbars, menus) use dark mode
  [DllImport("uxtheme.dll", EntryPoint="#135")] public static extern int SetPreferredAppMode(int mode);
  [DllImport("uxtheme.dll", EntryPoint="#133")] public static extern bool AllowDarkModeForWindow(IntPtr h, bool allow);
  [DllImport("uxtheme.dll", EntryPoint="#136")] public static extern void FlushMenuThemes();
  public static void DarkApp() { try { SetPreferredAppMode(2); FlushMenuThemes(); } catch {} }
  public static void DarkWindow(IntPtr h, string theme) { try { AllowDarkModeForWindow(h, true); } catch {} SetWindowTheme(h, theme, null); }
}
// Flat dark progress bar (the Windows one can't be recoloured)
public class CSProgress : System.Windows.Forms.Control {
  int v, max = 100;
  public CSProgress() { SetStyle(System.Windows.Forms.ControlStyles.AllPaintingInWmPaint | System.Windows.Forms.ControlStyles.OptimizedDoubleBuffer | System.Windows.Forms.ControlStyles.UserPaint | System.Windows.Forms.ControlStyles.ResizeRedraw, true); }
  public int Minimum { get; set; }
  public int Maximum { get { return max; } set { max = Math.Max(1, value); Invalidate(); } }
  public int Value { get { return v; } set { v = Math.Max(0, Math.Min(max, value)); Invalidate(); } }
  public System.Drawing.Color BarColor = System.Drawing.Color.FromArgb(96, 205, 255);
  public System.Drawing.Color TrackColor = System.Drawing.Color.FromArgb(58, 58, 58);
  protected override void OnPaint(System.Windows.Forms.PaintEventArgs e) {
    var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
    g.Clear(Parent != null ? Parent.BackColor : BackColor);
    int h = Math.Min(4, Height); int y = (Height - h) / 2;
    using (var b = new System.Drawing.SolidBrush(TrackColor)) g.FillRectangle(b, 0, y, Width, h);
    int w = (int)((long)Width * v / max);
    if (w > 0) using (var b = new System.Drawing.SolidBrush(BarColor)) g.FillRectangle(b, 0, y, w, h);
  }
}
"@ -ReferencedAssemblies System.Windows.Forms, System.Drawing
}
[CSNative]::DarkApp()

function C($hex) { [Drawing.ColorTranslator]::FromHtml($hex) }
$Theme = @{
  Bg=(C '#202020'); Side=(C '#1A1A1A'); Card=(C '#2B2B2B'); CardHover=(C '#323232'); Tile=(C '#333333'); TileHover=(C '#3C3C3C')
  Input=(C '#1C1C1C'); Button=(C '#2D2D2D'); ButtonHover=(C '#383838'); ButtonDown=(C '#262626'); Border=(C '#3D3D3D')
  Text=(C '#FFFFFF'); Sub=(C '#A6A6A6'); Faint=(C '#6E6E6E')
  Accent=(C '#60CDFF'); AccentHover=(C '#7AD6FF'); AccentDown=(C '#4BB8EA'); OnAccent=(C '#000000')
  Ok=(C '#6CCB5F'); Warn=(C '#FCE100'); Bad=(C '#FF99A4'); Info=(C '#A6A6A6'); Select=(C '#3A3F46')
}
# Old light-theme colour names used around the app -> dark equivalents
$ThemeMap = @{ 'Firebrick'=$Theme.Bad; 'DarkOrange'=$Theme.Warn; 'ForestGreen'=$Theme.Ok; 'SeaGreen'=$Theme.Ok; 'DimGray'=$Theme.Sub; 'Gray'=$Theme.Faint; 'Black'=$Theme.Text; 'WindowText'=$Theme.Text; 'ControlText'=$Theme.Text }
function Map-Color($c) { if ($c.IsEmpty) { return $Theme.Text }; $m = $ThemeMap[$c.Name]; if ($m) { $m } else { $c } }

$fams = @([Drawing.FontFamily]::Families | ForEach-Object Name)
$UiFontName = if ($fams -contains 'Segoe UI Variable Text') { 'Segoe UI Variable Text' } else { 'Segoe UI' }
$DisplayFontName = if ($fams -contains 'Segoe UI Variable Display') { 'Segoe UI Variable Display' } else { 'Segoe UI' }
$IconFontName = if ($fams -contains 'Segoe Fluent Icons') { 'Segoe Fluent Icons' } else { 'Segoe MDL2 Assets' }
function UiFont([float]$size = 10, $style = 'Regular') { New-Object Drawing.Font($UiFontName, $size, [Drawing.FontStyle]$style) }
function DisplayFont([float]$size = 14, $style = 'Bold') { New-Object Drawing.Font($DisplayFontName, $size, [Drawing.FontStyle]$style) }
function IconFont([float]$size = 14) { New-Object Drawing.Font($IconFontName, $size) }
$Glyph = @{ Home=[char]0xE80F; Junk=[char]0xE74D; Drive=[char]0xEDA2; Registry=[char]0xE8F1; Link=[char]0xE71B; Network=[char]0xE968; Repair=[char]0xE90F
  Update=[char]0xE895; Cpu=[char]0xE950; Memory=[char]0xE964; Gpu=[char]0xE7F4; Disk=[char]0xEDA2; Net=[char]0xE701; Battery=[char]0xE83F; Pc=[char]0xE7F8
  Clean=[char]0xE74D; Space=[char]0xE8B7; Speed=[char]0xEC4A; Shield=[char]0xE83D; Restore=[char]0xE777; Heart=[char]0xE95E; Report=[char]0xE9F9 }

function Set-DoubleBuffered($c) { [Windows.Forms.Control].GetProperty('DoubleBuffered', [Reflection.BindingFlags]'NonPublic,Instance').SetValue($c, $true, $null) }
# Run native styling once the control has a window handle
$script:HandleActions = @{}
function On-Handle($c, [scriptblock]$sb) {
  if ($c.IsHandleCreated) { try { & $sb $c } catch {}; return }
  if (-not $script:HandleActions.ContainsKey($c)) { $script:HandleActions[$c] = New-Object System.Collections.ArrayList; $c.Add_HandleCreated({ param($s, $e) foreach ($a in $script:HandleActions[$s]) { try { & $a $s } catch {} } }) }
  [void]$script:HandleActions[$c].Add($sb)
}
function Get-RoundPath([Drawing.Rectangle]$r, [int]$rad) {
  $p = New-Object Drawing.Drawing2D.GraphicsPath; $d = $rad * 2
  if ($r.Width -le $d -or $r.Height -le $d) { $p.AddRectangle($r); return $p }
  $p.AddArc($r.X, $r.Y, $d, $d, 180, 90); $p.AddArc($r.Right - $d - 1, $r.Y, $d, $d, 270, 90)
  $p.AddArc($r.Right - $d - 1, $r.Bottom - $d - 1, $d, $d, 0, 90); $p.AddArc($r.X, $r.Bottom - $d - 1, $d, $d, 90, 90); $p.CloseFigure(); $p
}
# Rounded card: a panel clipped to a rounded rectangle
$script:RoundR = @{}
function Update-Round($s) { $rad = $script:RoundR[$s]; if ($s.Width -gt 0 -and $s.Height -gt 0) { $s.Region = New-Object Drawing.Region (Get-RoundPath (New-Object Drawing.Rectangle 0, 0, $s.Width, $s.Height) $rad) } }
function Set-Rounded($c, [int]$rad = 8) {
  $script:RoundR[$c] = $rad; Update-Round $c; $c.Add_Resize({ param($s, $e) Update-Round $s })
}
function New-Card([string]$dock = 'Fill', [string]$pad = '16,14,16,14') {
  $p = New-Object Windows.Forms.Panel -Property @{Dock=$dock; BackColor=$Theme.Card; Padding=$pad; Margin='0,0,12,12'}
  Set-Rounded $p 8; $p
}

# Cached fonts for painting (avoid creating GDI objects on every repaint)
$CSFonts = @{ Hdr=(UiFont 9 'Bold'); Nav=(UiFont 10); NavIcon=(IconFont 13) }
$script:PrimaryButtons = New-Object System.Collections.Generic.List[object]
function Set-Primary($b) { $script:PrimaryButtons.Add($b) }

# ---------------------------------------------------------------- per-control styling
function Style-Button($b) {
  $b.FlatStyle = 'Flat'; $b.UseVisualStyleBackColor = $false; $b.Cursor = 'Hand'; $b.Font = UiFont 10
  Set-ButtonColors $b; $b.Add_EnabledChanged({ param($s, $e) Set-ButtonColors $s })
  Set-Rounded $b 4
}
function Set-ButtonColors($s) {
    if ($script:PrimaryButtons.Contains($s) -and $s.Enabled) { $s.BackColor = $Theme.Accent; $s.ForeColor = $Theme.OnAccent; $s.FlatAppearance.BorderColor = $Theme.Accent; $s.FlatAppearance.MouseOverBackColor = $Theme.AccentHover; $s.FlatAppearance.MouseDownBackColor = $Theme.AccentDown }
    else { $s.BackColor = $Theme.Button; $s.ForeColor = $Theme.Text; $s.FlatAppearance.BorderColor = $Theme.Border; $s.FlatAppearance.MouseOverBackColor = $Theme.ButtonHover; $s.FlatAppearance.MouseDownBackColor = $Theme.ButtonDown }
}
function Style-Check($cb) {
  $cb.ForeColor = $Theme.Text; $cb.Cursor = 'Hand'
  # Paint a dark Windows 11 style box over the classic one
  $cb.Add_Paint({ param($s, $e)
    $g = $e.Graphics; $g.SmoothingMode = 'AntiAlias'
    $y = [int](($s.Height - 16) / 2); if ($s.CheckAlign -ne 'MiddleLeft') { $y = 1 }
    $bg = New-Object Drawing.SolidBrush $s.Parent.BackColor; $g.FillRectangle($bg, 0, $y - 1, 18, 18); $bg.Dispose()
    $box = New-Object Drawing.Rectangle 0, $y, 15, 15; $path = Get-RoundPath $box 3
    if ($s.Checked) {
      $br = New-Object Drawing.SolidBrush $(if ($s.Enabled) { $Theme.Accent } else { $Theme.Faint }); $g.FillPath($br, $path); $br.Dispose()
      $pen = New-Object Drawing.Pen $Theme.OnAccent, 1.8; $g.DrawLines($pen, [Drawing.PointF[]]@((New-Object Drawing.PointF 3.5, ($y + 7.5)), (New-Object Drawing.PointF 6.5, ($y + 10.5)), (New-Object Drawing.PointF 11.5, ($y + 4.5)))); $pen.Dispose()
    } else { $pen = New-Object Drawing.Pen $(if ($s.Enabled) { $Theme.Sub } else { $Theme.Faint }), 1; $g.DrawPath($pen, $path); $pen.Dispose() }
  })
}
function Style-ListView($lv) {
  $lv.BackColor = $Theme.Card; $lv.ForeColor = $Theme.Text; $lv.BorderStyle = 'None'; $lv.OwnerDraw = $true; $lv.Font = UiFont 10
  Set-DoubleBuffered $lv
  $lv.Add_DrawColumnHeader({ param($s, $e)
    $b = New-Object Drawing.SolidBrush $Theme.Bg; $e.Graphics.FillRectangle($b, $e.Bounds); $b.Dispose()
    $r = $e.Bounds; $r.X += 6; $r.Width -= 6
    [Windows.Forms.TextRenderer]::DrawText($e.Graphics, $e.Header.Text, $CSFonts.Hdr, $r, $Theme.Sub, [Windows.Forms.TextFormatFlags]'VerticalCenter,Left,EndEllipsis')
  })
  $lv.Add_DrawItem({ param($s, $e) })   # rows are painted cell by cell below
  $lv.Add_DrawSubItem({ param($s, $e)
    $g = $e.Graphics; $item = $e.Item; $sel = $item.Selected
    $bg = if ($sel) { $Theme.Select } else { $s.BackColor }
    $b = New-Object Drawing.SolidBrush $bg; $g.FillRectangle($b, $e.Bounds); $b.Dispose()
    $r = $e.Bounds; $x = $r.X + 6
    if ($e.ColumnIndex -eq 0) {
      if ($s.CheckBoxes) {
        $g.SmoothingMode = 'AntiAlias'; $box = New-Object Drawing.Rectangle ($r.X + 4), ($r.Y + [int](($r.Height - 15) / 2)), 15, 15; $path = Get-RoundPath $box 3
        $disabled = $item.ForeColor.Name -in 'Gray','GrayText'
        if ($item.Checked) { $br = New-Object Drawing.SolidBrush $Theme.Accent; $g.FillPath($br, $path); $br.Dispose()
          $pen = New-Object Drawing.Pen $Theme.OnAccent, 1.8; $g.DrawLines($pen, [Drawing.PointF[]]@((New-Object Drawing.PointF ($box.X + 3.5), ($box.Y + 7.5)), (New-Object Drawing.PointF ($box.X + 6.5), ($box.Y + 10.5)), (New-Object Drawing.PointF ($box.X + 11.5), ($box.Y + 4.5)))); $pen.Dispose() }
        elseif (-not $disabled) { $pen = New-Object Drawing.Pen $Theme.Sub, 1; $g.DrawPath($pen, $path); $pen.Dispose() }
        $g.SmoothingMode = 'Default'; $x = $r.X + 26
      }
    }
    $col = if ($e.ColumnIndex -eq 0 -or $item.UseItemStyleForSubItems) { $item.ForeColor } else { $e.SubItem.ForeColor }
    $font = if ($e.ColumnIndex -eq 0 -or $item.UseItemStyleForSubItems) { $item.Font } else { $e.SubItem.Font }
    $tr = New-Object Drawing.Rectangle $x, $r.Y, ([math]::Max(0, $r.Right - $x - 4)), $r.Height
    [Windows.Forms.TextRenderer]::DrawText($g, $e.SubItem.Text, $font, $tr, (Map-Color $col), [Windows.Forms.TextFormatFlags]'VerticalCenter,Left,EndEllipsis,NoPrefix')
  })
  $lv.Add_ItemChecked({ param($s, $e) $s.Invalidate($e.Item.Bounds) })
  On-Handle $lv { param($c)
    [CSNative]::DarkWindow($c.Handle, "DarkMode_Explorer")
    $hdr = [CSNative]::SendMessage($c.Handle, 0x101F, [IntPtr]::Zero, [IntPtr]::Zero)   # LVM_GETHEADER
    if ($hdr -ne [IntPtr]::Zero) { [CSNative]::DarkWindow($hdr, "DarkMode_ItemsView") }
  }
}
function Style-Progress($pb) {
  On-Handle $pb { param($c)
    [void][CSNative]::SetWindowTheme($c.Handle, "", "")
    [void][CSNative]::SendMessage($c.Handle, 0x0409, [IntPtr]::Zero, [IntPtr][int]([Drawing.ColorTranslator]::ToWin32($Theme.Accent)))   # PBM_SETBARCOLOR
    [void][CSNative]::SendMessage($c.Handle, 0x2001, [IntPtr]::Zero, [IntPtr][int]([Drawing.ColorTranslator]::ToWin32($Theme.Card)))     # PBM_SETBKCOLOR
  }
}

function Apply-Theme($root) {
  foreach ($c in $root.Controls) {
    switch ($c) {
      { $_ -is [Windows.Forms.Button] }      { Style-Button $c; break }
      { $_ -is [Windows.Forms.CheckBox] }    { Style-Check $c; break }
      { $_ -is [Windows.Forms.ListView] }    { Style-ListView $c; break }
      { $_ -is [Windows.Forms.ProgressBar] } { Style-Progress $c; break }
      { $_ -is [CSProgress] } { $c.BarColor = $Theme.Accent; $c.TrackColor = (C '#3A3A3A'); break }
      { $_ -is [Windows.Forms.TextBox] }     { $c.BackColor = $Theme.Input; $c.ForeColor = $Theme.Text; $c.BorderStyle = 'FixedSingle'; On-Handle $c { param($x) [CSNative]::DarkWindow($x.Handle, "DarkMode_Explorer") }; break }
      { $_ -is [Windows.Forms.ComboBox] }    { $c.FlatStyle = 'Flat'; $c.BackColor = $Theme.Button; $c.ForeColor = $Theme.Text; On-Handle $c { param($x) [CSNative]::DarkWindow($x.Handle, "DarkMode_CFD") }; break }
      { $_ -is [Windows.Forms.TabPage] }     { $c.BackColor = $Theme.Bg; $c.ForeColor = $Theme.Text; $c.Padding = '24,18,24,16'; break }
      { $_ -is [Windows.Forms.Label] } {
        if (-not $c.ForeColor.IsEmpty -and $ThemeMap[$c.ForeColor.Name]) { $c.ForeColor = $ThemeMap[$c.ForeColor.Name] }
        if ($c.Font.Bold -and $c.Font.Size -ge 11 -and $c.Font.FontFamily.Name -notin $DisplayFontName) { $c.Font = DisplayFont 14 'Bold' }
        break }
      { $_ -is [Windows.Forms.ScrollableControl] -and $_.AutoScroll } { On-Handle $c { param($x) [CSNative]::DarkWindow($x.Handle, "DarkMode_Explorer") } }
    }
    if ($c.HasChildren -and -not ($c -is [Windows.Forms.ListView])) { Apply-Theme $c }
  }
}

# Dark styling for pop-up windows created after start-up
function Use-DarkDialog($f) {
  $f.BackColor = $Theme.Bg; $f.ForeColor = $Theme.Text
  Apply-Theme $f; Set-DarkTitleBar $f
}

# ---------------------------------------------------------------- window shell: dark title bar, sidebar, hidden tab strip
function Set-DarkTitleBar($f) {
  On-Handle $f { param($x)
    $one = 1; [void][CSNative]::DwmSetWindowAttribute($x.Handle, 20, [ref]$one, 4); [void][CSNative]::DwmSetWindowAttribute($x.Handle, 19, [ref]$one, 4)
    $cap = [Drawing.ColorTranslator]::ToWin32($Theme.Side); [void][CSNative]::DwmSetWindowAttribute($x.Handle, 35, [ref]$cap, 4)   # caption colour (Windows 11)
  }
}

$script:NavItems = @()
function New-NavItem($page, $glyph) {
  $p = New-Object Windows.Forms.Panel -Property @{Dock='Top'; Height=40; Cursor='Hand'; BackColor=$Theme.Side; Margin='0,0,0,0'}
  Set-DoubleBuffered $p
  $p.Tag = @{ Page=$page; Glyph=$glyph; Hover=$false }
  $p.Add_Paint({ param($s, $e)
    $g = $e.Graphics; $g.SmoothingMode = 'AntiAlias'; $t = $s.Tag; $sel = ($tabs.SelectedTab -eq $t.Page)
    if ($sel -or $t.Hover) {
      $r = New-Object Drawing.Rectangle 8, 3, ($s.Width - 16), ($s.Height - 6)
      $b = New-Object Drawing.SolidBrush $(if ($sel) { $Theme.Card } else { (C '#262626') }); $g.FillPath($b, (Get-RoundPath $r 5)); $b.Dispose()
      if ($sel) { $a = New-Object Drawing.SolidBrush $Theme.Accent; $g.FillPath($a, (Get-RoundPath (New-Object Drawing.Rectangle 8, 12, 3, ($s.Height - 24)) 1)); $a.Dispose() }
    }
    [Windows.Forms.TextRenderer]::DrawText($g, [string]$t.Glyph, $CSFonts.NavIcon, (New-Object Drawing.Rectangle 22, 0, 26, $s.Height), $Theme.Text, [Windows.Forms.TextFormatFlags]'VerticalCenter,HorizontalCenter')
    [Windows.Forms.TextRenderer]::DrawText($g, $t.Page.Text, $CSFonts.Nav, (New-Object Drawing.Rectangle 56, 0, ($s.Width - 60), $s.Height), $Theme.Text, [Windows.Forms.TextFormatFlags]'VerticalCenter,Left,EndEllipsis')
  })
  $p.Add_MouseEnter({ param($s, $e) $s.Tag.Hover = $true; $s.Invalidate() })
  $p.Add_MouseLeave({ param($s, $e) $s.Tag.Hover = $false; $s.Invalidate() })
  $p.Add_Click({ param($s, $e) $tabs.SelectedTab = $s.Tag.Page })
  $script:NavItems += $p
  $p
}

function Fit-Tabs { $h = $script:HostP; $tabs.Bounds = New-Object Drawing.Rectangle -4, -6, ($h.ClientSize.Width + 8), ($h.ClientSize.Height + 10) }
function Initialize-Shell {
  # Window size: comfortable on a 1366x768 laptop, never bigger than the screen
  $wa = [Windows.Forms.Screen]::PrimaryScreen.WorkingArea
  $form.MinimumSize = New-Object Drawing.Size ([math]::Min(960, $wa.Width)), ([math]::Min(640, $wa.Height))
  $form.Size = New-Object Drawing.Size ([math]::Min(1200, $wa.Width)), ([math]::Min(800, $wa.Height))
  $form.BackColor = $Theme.Bg; $form.ForeColor = $Theme.Text; $form.Font = UiFont 10
  Set-DarkTitleBar $form

  # Sidebar
  $side = New-Object Windows.Forms.Panel -Property @{Dock='Left'; Width=220; BackColor=$Theme.Side; Padding='0,8,0,8'}
  $brand = New-Object Windows.Forms.Panel -Property @{Dock='Top'; Height=64; BackColor=$Theme.Side}
  $logo = New-Object Windows.Forms.PictureBox -Property @{Size='28,28'; Location='20,18'; SizeMode='StretchImage'}
  try { $ico = Join-Path (Split-Path $PSScriptRoot) "CleanSweep.ico"; $logo.Image = (New-Object Drawing.Icon $ico, 32, 32).ToBitmap() } catch { try { $logo.Image = $form.Icon.ToBitmap() } catch {} }
  $bt = New-Object Windows.Forms.Label -Property @{Text="CleanSweep"; Location='56,13'; AutoSize=$true; Font=(DisplayFont 13 'Bold'); ForeColor=$Theme.Text}
  $bv = New-Object Windows.Forms.Label -Property @{Text="Version $Version"; Location='57,37'; AutoSize=$true; Font=(UiFont 8.5); ForeColor=$Theme.Sub}
  $brand.Controls.AddRange(@($logo, $bt, $bv))
  $icons = @{ 'Dashboard'=$Glyph.Home; 'Cleanup'=$Glyph.Junk; 'Drives'=$Glyph.Drive; 'Registry'=$Glyph.Registry; 'Broken Shortcuts'=$Glyph.Link; 'Network Optimizer'=$Glyph.Network; 'Repair'=$Glyph.Repair; 'Updates & settings'=$Glyph.Update }
  $pages = @($tabs.TabPages | ForEach-Object { $_ })
  # Dock=Top stacks in reverse, so add the last page first
  for ($i = $pages.Count - 1; $i -ge 0; $i--) { $g = $icons[$pages[$i].Text]; if (-not $g) { $g = $Glyph.Pc }; $side.Controls.Add((New-NavItem $pages[$i] $g)) }
  $side.Controls.Add($brand)
  $foot = New-Object Windows.Forms.Label -Property @{Dock='Bottom'; Height=40; Text="Monitor - Clean - Repair`nNothing changes without your click"; ForeColor=$Theme.Faint; Font=(UiFont 8); Padding='20,0,0,0'}
  $side.Controls.Add($foot)

  # Hide the tab strip: pages switch from the sidebar
  $tabs.Appearance = 'FlatButtons'; $tabs.ItemSize = New-Object Drawing.Size 0, 1; $tabs.SizeMode = 'Fixed'
  $hostP = New-Object Windows.Forms.Panel -Property @{Dock='Fill'; BackColor=$Theme.Bg}
  $form.Controls.Remove($tabs); $tabs.Dock = 'None'; $hostP.Controls.Add($tabs)
  $script:HostP = $hostP
  $hostP.Add_Resize({ Fit-Tabs })
  $form.Controls.Add($hostP); $form.Controls.Add($side); Fit-Tabs
  $tabs.Add_SelectedIndexChanged({ foreach ($n in $script:NavItems) { $n.Invalidate() } })

  Apply-Theme $form
}
