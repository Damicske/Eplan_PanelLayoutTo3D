Option Strict On
Option Infer On

Imports System.Drawing
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Text
Imports System.Windows.Forms
Imports Eplan.EplApi.DataModel
Imports EFunction = Eplan.EplApi.DataModel.Function
Imports E3D = Eplan.EplApi.DataModel.E3D

''' <summary>
''' The conversion dialog. Everything to choose from comes from the project and the parts database: the cabinets
''' (the cabinet devices of the project, the cabinets in the 3D layout, the cabinet parts in the database) and their
''' mounting panels, the 2D plate candidates on the page, the
''' type of every part placement from its product group, and for the rail lines and duct rectangles the rails and
''' ducts in the database (ducts as wide as the rectangle; the one used most in the project first).
''' </summary>
Public Class FrmLayoutTo3D
    Inherits Form

    Private Shared ReadOnly BackGrey As Color = Color.FromArgb(&HC0, &HC0, &HC0)
    ''' <summary>Per <see cref="ItemKind"/> (index = value); "Cabinet" is not offered in the grid.</summary>
    Private Shared ReadOnly KindNames As String() = {"Device", "Rail", "Duct", "Mounting plate", "Skip", "Cabinet", "Drill hole"}

    Private ReadOnly _prj As Project
    Private ReadOnly _page As Page
    Private ReadOnly _selected As List(Of EFunction)
    Private _items As New List(Of LayoutItem)()
    Private _plates As New List(Of LayoutItem)()
    Private _db As Eplan.EplApi.MasterData.MDPartsDatabase
    Private _catalog As New List(Of PartInfo)()
    Private _usage As New Dictionary(Of String, Integer)()
    Private ReadOnly _choices As New Dictionary(Of LayoutItem, List(Of PartInfo))()
    Private ReadOnly _log As New StringBuilder()
    Private _filling As Boolean

    Private ReadOnly lblPage As New Label With {.AutoSize = True, .Font = New Font(SystemFonts.DefaultFont, FontStyle.Bold)}
    Private ReadOnly cboCabinet As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 620}
    Private ReadOnly btnDbCabinets As New Button With {.Text = "All cabinets from parts DB", .Width = 180}
    Private ReadOnly cboPanel As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 620}
    Private ReadOnly cboSpace As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 520}
    Private ReadOnly cboPlate As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 520}
    Private ReadOnly numScale As New NumericUpDown With {.DecimalPlaces = 4, .Minimum = 0.0001D, .Maximum = 1000D, .Value = 1D, .Increment = 0.1D, .Width = 90}
    Private ReadOnly chkAutoScale As New CheckBox With {.Text = "from the plate", .Checked = True, .AutoSize = True}
    Private ReadOnly numRailColor As New NumericUpDown With {.Minimum = 0, .Maximum = 255, .Value = 1, .Width = 60}
    Private ReadOnly numGap As New NumericUpDown With {.Minimum = 0, .Maximum = 100, .Value = 10, .DecimalPlaces = 1, .Width = 60}
    Private ReadOnly chkRails As New CheckBox With {.Text = "Rails from lines, colour", .Checked = True, .AutoSize = True}
    Private ReadOnly chkDucts As New CheckBox With {.Text = "Ducts from filled rectangles", .Checked = True, .AutoSize = True}
    Private ReadOnly btnReread As New Button With {.Text = "Read page again", .Width = 140}
    Private ReadOnly chkSnap As New CheckBox With {.Text = "Devices onto the rail under them", .Checked = True, .AutoSize = True}
    Private ReadOnly chkSkip As New CheckBox With {.Text = "Skip devices already in 3D", .Checked = True, .AutoSize = True}
    Private ReadOnly chkNames As New CheckBox With {.Text = "Give the 3D part the device tag", .Checked = True, .AutoSize = True}
    ' "Place" filter on the Standard tab (not saved): which kinds this Convert places
    Private ReadOnly chkPlaceDevices As New CheckBox With {.Text = "Devices", .Checked = True, .AutoSize = True}
    Private ReadOnly chkPlaceRails As New CheckBox With {.Text = "Rails", .Checked = True, .AutoSize = True}
    Private ReadOnly chkPlaceDucts As New CheckBox With {.Text = "Ducts", .Checked = True, .AutoSize = True}
    Private ReadOnly chkPlaceHoles As New CheckBox With {.Text = "Drill holes", .Checked = True, .AutoSize = True}
    Private ReadOnly chkHoles As New CheckBox With {.Text = "Drill hole in the mounting plate at the middle of 2D objects without a part, diameter", .Checked = False, .AutoSize = True}
    Private ReadOnly numHoleDia As New NumericUpDown With {.Minimum = 0.1D, .Maximum = 100D, .Value = 1D, .DecimalPlaces = 1, .Increment = 0.5D, .Width = 60}
    Private ReadOnly chkFlipX As New CheckBox With {.Text = "Mirror left / right", .AutoSize = True}
    Private ReadOnly chkFlipY As New CheckBox With {.Text = "Mirror top / bottom", .AutoSize = True}
    Private ReadOnly numShiftX As New NumericUpDown With {.Minimum = -2000, .Maximum = 2000, .DecimalPlaces = 1, .Width = 70}
    Private ReadOnly numShiftY As New NumericUpDown With {.Minimum = -2000, .Maximum = 2000, .DecimalPlaces = 1, .Width = 70}
    Private ReadOnly grid As New DataGridView With {.AllowUserToAddRows = False, .AllowUserToDeleteRows = False, .RowHeadersVisible = False,
                                                     .SelectionMode = DataGridViewSelectionMode.FullRowSelect, .BackgroundColor = Color.FromArgb(&HB0, &HB0, &HB0),
                                                     .AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None}
    Private ReadOnly txtLog As New TextBox With {.Multiline = True, .ScrollBars = ScrollBars.Vertical, .ReadOnly = True, .BackColor = Color.FromArgb(&HD8, &HD8, &HD8), .Font = New Font("Consolas", 8.5F)}
    Private ReadOnly btnConvert As New Button With {.Text = "Convert to 3D", .Width = 140, .Height = 30}
    Private ReadOnly btnClose As New Button With {.Text = "Close", .Width = 100, .Height = 30, .DialogResult = DialogResult.Cancel}
    Private ReadOnly btnSaveLog As New Button With {.Text = "Save log to file...", .Width = 140, .Height = 30}
    Private ReadOnly lblInfo As New Label With {.AutoSize = True}
    Private ReadOnly tabs As New TabControl With {.Dock = DockStyle.Top, .Height = 200}
    Private ReadOnly cboDuctMake As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 300}
    Private ReadOnly cboRail As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 520}
    ''' <summary>Default duct make / DIN rail from the settings file (applied once the parts database is read).</summary>
    Private _savedDuctMake As String = ""
    Private _savedRail As String = ""

    Private colUse, colType, colName, colPart, colGroup, colSize, colTurn, colIn3D, colStatus As DataGridViewColumn
    Private Shared ReadOnly TurnNames As String() = {"", "90", "180", "270", "flip"}
    ''' <summary>Turn per part number ("Turn" column), kept in PanelLayoutTo3D.turns.txt in DataFolder.</summary>
    Private ReadOnly _turns As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)

    Public Sub New(prj As Project, page As Page, selected As List(Of EFunction))
        _prj = prj
        _page = page
        _selected = If(selected, New List(Of EFunction)())
        Text = "2D panel layout -> 3D"
        BackColor = BackGrey
        StartPosition = FormStartPosition.CenterParent
        Size = New Size(1280, 900)
        MinimumSize = New Size(1000, 700)
        CancelButton = btnClose
        Try
            Using st = GetType(FrmLayoutTo3D).Assembly.GetManifestResourceStream("PanelLayoutTo3D.ico")
                If st IsNot Nothing Then Me.Icon = New System.Drawing.Icon(st)
            End Using
        Catch
        End Try
        BuildLayout()
        BuildGrid()
        LoadSettings()
        AddHandler FormClosing, Sub() SaveSettings()
    End Sub

    ' ---------------------------------------------------------------- layout

    Private Sub BuildLayout()
        ' the page name above two tabs: "Standard" (what goes where) and "Settings" (how; saved)
        Dim head As New Panel With {.Dock = DockStyle.Top, .Height = 28, .Padding = New Padding(10, 8, 8, 0)}
        lblPage.Location = New Point(10, 8)
        head.Controls.Add(lblPage)

        Dim std = NewTable()
        Dim row = Sub(t As TableLayoutPanel, caption As String, ctl As Control)
                      t.Controls.Add(New Label With {.Text = caption, .AutoSize = True, .Anchor = AnchorStyles.Top Or AnchorStyles.Left, .Margin = New Padding(3, 7, 3, 3)})
                      t.Controls.Add(ctl)
                  End Sub
        row(std, "Cabinet:", Flow(cboCabinet, btnDbCabinets))
        row(std, "Surface (panel, door, side):", cboPanel)
        row(std, "Installation space (new cabinet):", cboSpace)
        row(std, "2D mounting plate:", cboPlate)
        row(std, "Place:", Flow(chkPlaceDucts, chkPlaceRails, chkPlaceDevices, chkPlaceHoles,
                                New Label With {.Text = "(only the ticked rows of these types)", .AutoSize = True, .Margin = New Padding(12, 7, 3, 3)}))

        Dim st = NewTable()
        row(st, "Scale:", Flow(numScale, chkAutoScale, Spacer(20), chkFlipX, chkFlipY, Spacer(20),
                               New Label With {.Text = "shift right", .AutoSize = True, .Margin = New Padding(3, 7, 3, 3)}, numShiftX,
                               New Label With {.Text = "up", .AutoSize = True, .Margin = New Padding(3, 7, 3, 3)}, numShiftY,
                               New Label With {.Text = "mm", .AutoSize = True, .Margin = New Padding(3, 7, 3, 3)}))
        row(st, "2D page:", Flow(chkRails, numRailColor, New Label With {.Text = "(1 = red)", .AutoSize = True, .Margin = New Padding(3, 7, 12, 3)},
                                 New Label With {.Text = "rail ends onto ducts within", .AutoSize = True, .Margin = New Padding(3, 7, 3, 3)}, numGap,
                                 New Label With {.Text = "mm", .AutoSize = True, .Margin = New Padding(3, 7, 12, 3)}, chkDucts, btnReread))
        row(st, "Options:", Flow(chkSnap, chkSkip, chkNames))
        row(st, "Drill holes:", Flow(chkHoles, numHoleDia, New Label With {.Text = "mm", .AutoSize = True, .Margin = New Padding(3, 7, 3, 3)}))
        row(st, "Default duct make:", Flow(cboDuctMake, New Label With {.Text = "(first choice for duct rectangles of its width)", .AutoSize = True, .Margin = New Padding(3, 7, 3, 3)}))
        row(st, "Default DIN rail:", Flow(cboRail, New Label With {.Text = "(first choice for rail lines)", .AutoSize = True, .Margin = New Padding(3, 7, 3, 3)}))

        tabs.TabPages.Add(NewPage("Standard", std))
        tabs.TabPages.Add(NewPage("Settings", st))

        Dim bottom As New FlowLayoutPanel With {.Dock = DockStyle.Bottom, .Height = 44, .FlowDirection = FlowDirection.RightToLeft, .Padding = New Padding(8)}
        bottom.Controls.Add(btnClose)
        bottom.Controls.Add(btnConvert)
        bottom.Controls.Add(btnSaveLog)
        bottom.Controls.Add(lblInfo)
        lblInfo.Margin = New Padding(3, 9, 20, 3)

        txtLog.Dock = DockStyle.Bottom
        txtLog.Height = 170
        grid.Dock = DockStyle.Fill

        Controls.Add(grid)
        Controls.Add(txtLog)
        Controls.Add(bottom)
        Controls.Add(tabs)
        Controls.Add(head)

        AddHandler cboCabinet.SelectedIndexChanged, AddressOf CabinetChanged
        AddHandler cboPanel.SelectedIndexChanged, Sub() UpdateScale()
        AddHandler cboPlate.SelectedIndexChanged, Sub() SelectBestSurface()
        AddHandler chkAutoScale.CheckedChanged, Sub() UpdateScale()
        AddHandler btnDbCabinets.Click, AddressOf AddDbCabinets
        AddHandler btnReread.Click, Sub() ReadPage()
        AddHandler btnConvert.Click, AddressOf Convert
        AddHandler btnSaveLog.Click, AddressOf SaveLogAs
        AddHandler cboDuctMake.SelectedIndexChanged, Sub() DefaultsChanged()
        ' the drill hole rows come from reading the page: read again when switched
        AddHandler chkHoles.CheckedChanged, Sub()
                                                If Visible Then ReadPage()
                                            End Sub
        AddHandler cboRail.SelectedIndexChanged, Sub() DefaultsChanged()
    End Sub

    Private Shared Function NewTable() As TableLayoutPanel
        Dim t As New TableLayoutPanel With {.Dock = DockStyle.Fill, .ColumnCount = 2, .Padding = New Padding(4), .BackColor = BackGrey}
        t.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
        t.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        Return t
    End Function

    Private Shared Function NewPage(caption As String, content As Control) As TabPage
        Dim p As New TabPage(caption) With {.BackColor = BackGrey, .UseVisualStyleBackColor = False}
        p.Controls.Add(content)
        Return p
    End Function

    ''' <summary>The tabs as high as the higher of the two pages.</summary>
    Protected Overrides Sub OnLoad(e As EventArgs)
        MyBase.OnLoad(e)
        Dim h = 0
        For Each p As TabPage In tabs.TabPages
            For Each c As Control In p.Controls
                h = Math.Max(h, c.GetPreferredSize(New Size(tabs.ClientSize.Width - 8, 0)).Height)
            Next
        Next
        If h > 0 Then tabs.Height = h + tabs.ItemSize.Height + 14
    End Sub

    Private Shared Function Flow(ParamArray ctls As Control()) As FlowLayoutPanel
        Dim f As New FlowLayoutPanel With {.AutoSize = True, .WrapContents = False, .Margin = New Padding(0)}
        f.Controls.AddRange(ctls)
        Return f
    End Function

    Private Shared Function Spacer(w As Integer) As Control
        Return New Panel With {.Width = w, .Height = 1}
    End Function

    Private Sub BuildGrid()
        colUse = New DataGridViewCheckBoxColumn With {.HeaderText = "Use", .Width = 40}
        Dim ct As New DataGridViewComboBoxColumn With {.HeaderText = "Type", .Width = 110, .FlatStyle = FlatStyle.Flat}
        ct.Items.AddRange(KindNames.Where(Function(k) k <> "Cabinet").ToArray())
        colType = ct
        colName = New DataGridViewTextBoxColumn With {.HeaderText = "Device tag", .Width = 140, .ReadOnly = True}
        colPart = New DataGridViewComboBoxColumn With {.HeaderText = "Part (parts database)", .Width = 260, .FlatStyle = FlatStyle.Flat}
        colGroup = New DataGridViewTextBoxColumn With {.HeaderText = "Product group", .Width = 230, .ReadOnly = True}
        colSize = New DataGridViewTextBoxColumn With {.HeaderText = "2D size / position", .Width = 190, .ReadOnly = True}
        colIn3D = New DataGridViewCheckBoxColumn With {.HeaderText = "In 3D", .Width = 45, .ReadOnly = True}
        colStatus = New DataGridViewTextBoxColumn With {.HeaderText = "Status", .AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, .ReadOnly = True}
        Dim cturn As New DataGridViewComboBoxColumn With {.HeaderText = "Turn", .Width = 60, .FlatStyle = FlatStyle.Flat,
                                                         .ToolTipText = "Extra turn after placing, remembered per part number: 90 / 180 / 270 on the plate, flip = front and back swapped"}
        cturn.Items.AddRange(TurnNames)
        colTurn = cturn
        grid.Columns.AddRange(colUse, colType, colName, colPart, colGroup, colSize, colTurn, colIn3D, colStatus)
        AddHandler grid.CurrentCellDirtyStateChanged, Sub()
                                                          If grid.IsCurrentCellDirty Then grid.CommitEdit(DataGridViewDataErrorContexts.Commit)
                                                      End Sub
        AddHandler grid.CellValueChanged, AddressOf CellChanged
        AddHandler grid.DataError, Sub(s, e) e.ThrowException = False
    End Sub

    ' ---------------------------------------------------------------- data

    Protected Overrides Sub OnShown(e As EventArgs)
        MyBase.OnShown(e)
        lblPage.Text = "Page " & SafePageName() & If(LayoutReader.IsPanelLayout(_page), "", "  (not a panel layout page)")
        Cursor = Cursors.WaitCursor
        Try
            Log("Reading the parts database ...")
            Application.DoEvents()
            _db = LayoutReader.OpenPartsDatabase()
            If _db Is Nothing Then Log("  No parts database: product groups unknown, no rails / ducts / cabinets to choose from.")
            _usage = LayoutReader.PartUsage(_prj)
            Log("  Settings, turns and log in " & DataFolder)
            LoadTurns()
            _catalog = LayoutReader.Catalog(_db, _usage)
            Log(String.Format("  {0} rails, {1} ducts, {2} cabinets in the parts database",
                              _catalog.Where(Function(p) p.Kind = ItemKind.Rail).Count(),
                              _catalog.Where(Function(p) p.Kind = ItemKind.Duct).Count(),
                              _catalog.Where(Function(p) p.Kind = ItemKind.Cabinet).Count()))
            FillDefaults()
            FillTargets()
            ReadPage()
        Catch ex As Exception
            Log("Error: " & ex.Message)
        Finally
            Cursor = Cursors.Default
        End Try
    End Sub

    Private Function SafePageName() As String
        Try
            Return _page.Name
        Catch
            Return "?"
        End Try
    End Function

    Private Class Choice
        Public Text As String
        ''' <summary>An existing cabinet in the 3D layout.</summary>
        Public Cabinet As E3D.Cabinet
        ''' <summary>The mounting panels in the 3D layout that are in no cabinet.</summary>
        Public Loose As Boolean
        ''' <summary>A new cabinet from this part, with this device tag.</summary>
        Public PartNr As String
        Public VariantNr As String
        Public CabName As String
        Public Panel As E3D.Placement3D
        Public Plane As E3D.Plane
        Public Space As E3D.InstallationSpace
        Public NewSpaceName As String
        Public Plate As LayoutItem
        Public Overrides Function ToString() As String
            Return Text
        End Function
    End Class

    Private ReadOnly _loosePanels As New List(Of E3D.Placement3D)()

    Private Sub AddSpace(sp As E3D.InstallationSpace)
        If sp Is Nothing Then Return
        For Each o In cboSpace.Items
            Dim c = DirectCast(o, Choice)
            If c.Space IsNot Nothing AndAlso c.Space.Equals(sp) Then Return
        Next
        cboSpace.Items.Add(New Choice With {.Text = Placer3D.SpaceName(sp), .Space = sp})
    End Sub

    ''' <summary>The page's structure: "+APS_201" for "+APS_201/8".</summary>
    Private Function PageStructure() As String
        Dim loc = ""
        Try
            Dim n = SafePageName()
            Dim k = n.LastIndexOf("/"c)
            If k > 0 Then loc = n.Substring(0, k)
            Dim amp = loc.IndexOf("&"c)
            If amp >= 0 Then loc = loc.Substring(0, amp)
        Catch
        End Try
        Return loc
    End Function

    ''' <summary>
    ''' The cabinets to choose from: first the cabinet devices of the project that are not yet in 3D (the cabinet is
    ''' usually already a device in the project), then the cabinets in the 3D layout, then the mounting panels that are
    ''' in no cabinet. "All cabinets from parts DB" adds the rest of the database.
    ''' </summary>
    Private Sub FillTargets()
        cboCabinet.Items.Clear()
        cboSpace.Items.Clear()
        _loosePanels.Clear()
        Dim cabinets As New List(Of E3D.Cabinet)()
        Dim all As E3D.Placement3D() = Nothing
        Try
            all = New DMObjectsFinder(_prj).GetPlacements3D(Nothing)
        Catch ex As Exception
            Log("3D layout not readable: " & ex.Message)
        End Try
        If all IsNot Nothing Then
            For Each p In all
                Try
                    ' an installation space has no parent: it is not "placed"
                    Dim sp = TryCast(p, E3D.InstallationSpace)
                    If sp IsNot Nothing Then
                        AddSpace(sp)
                        Continue For
                    End If
                    Try
                        AddSpace(p.InstallationSpace)
                    Catch
                    End Try
                    If Not p.IsPlaced Then Continue For
                    Dim cab = TryCast(p, E3D.Cabinet)
                    If cab IsNot Nothing Then
                        cabinets.Add(cab)
                        Continue For
                    End If
                    If Placer3D.IsSurfacePart(p) AndAlso Placer3D.CabinetOf(p) Is Nothing Then _loosePanels.Add(p)
                Catch
                End Try
            Next
        End If

        ' cabinet devices of the project
        Dim inLayout As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        For Each c In cabinets
            Try
                inLayout.Add(If(c.Name, ""))
            Catch
            End Try
        Next
        Dim fromProject = LayoutReader.ProjectCabinets(_prj, _catalog)
        For Each pc In fromProject
            If pc.Item1.Length > 0 AndAlso inLayout.Contains(pc.Item1) Then Continue For
            cboCabinet.Items.Add(New Choice With {
                .Text = "New from project: " & If(pc.Item1.Length > 0, pc.Item1, "(no device tag)") & "   " & pc.Item2.Text,
                .PartNr = pc.Item2.PartNr, .VariantNr = pc.Item2.VariantNr, .CabName = pc.Item1})
        Next
        ' cabinets in the 3D layout
        For Each c In cabinets.OrderBy(Function(x) CabinetText(x))
            cboCabinet.Items.Add(New Choice With {.Text = "In 3D: " & CabinetText(c), .Cabinet = c})
        Next
        If _loosePanels.Count > 0 Then
            cboCabinet.Items.Add(New Choice With {.Text = String.Format("In 3D: mounting panels in no cabinet ({0})", _loosePanels.Count), .Loose = True})
        End If
        Log(String.Format("  {0} cabinet device(s) in the project, {1} cabinet(s) in the 3D layout", fromProject.Count, cabinets.Count))

        ' a new space, named after the page's structure ("+APS_201"), and the default space
        Dim loc = PageStructure()
        ' named like EPLAN names it after the structure: "APS_201" for +APS_201 (no prefix signs)
        Dim newName = loc.TrimStart("="c, "+"c, "#"c).Replace("+", ".").Replace("=", ".").Replace("#", ".")
        If newName.Length = 0 Then newName = "Layout space"
        If loc.Length = 0 Then loc = newName
        cboSpace.Items.Add(New Choice With {.Text = "New installation space: " & newName, .NewSpaceName = newName})
        Dim pick = -1
        For i = 0 To cboSpace.Items.Count - 2
            Dim t = DirectCast(cboSpace.Items(i), Choice).Text.TrimStart("="c, "+"c, "#"c)
            If String.Equals(t, newName, StringComparison.OrdinalIgnoreCase) Then pick = i : Exit For
        Next
        If pick < 0 Then pick = 0
        cboSpace.SelectedIndex = pick
        If cboCabinet.Items.Count > 0 Then cboCabinet.SelectedIndex = DefaultCabinet()
    End Sub

    ''' <summary>
    ''' The cabinet that belongs to the page: one in 3D with the page's structure in its name (+TD_205 for
    ''' +TD_205/8), else one of the project with it, else the first in 3D, else the first.
    ''' </summary>
    Private Function DefaultCabinet() As Integer
        ' The page's location (+TD_203 for page +TD_203/4) in the entry's text. Cabinets in 3D show their
        ' installation space without the "+" ("[TD_203]  RIT.8205000"), so the name is matched on its own, as a whole
        ' word: TD_203 is found in "[TD_203]" and "+TD_203-PANEL1", not in "TD_2030".
        Dim loc = PageStructure()
        Dim core = loc
        Dim plus = loc.LastIndexOf("+"c)
        If plus >= 0 Then core = loc.Substring(plus + 1)
        core = core.Trim("="c, "+"c, "#"c, " "c)
        Dim pattern = If(core.Length > 0, "(?<![A-Za-z0-9_])" & System.Text.RegularExpressions.Regex.Escape(core) & "(?![A-Za-z0-9_])", "")
        Dim firstIn3D = -1
        For pass = 0 To 1
            For i = 0 To cboCabinet.Items.Count - 1
                Dim c = DirectCast(cboCabinet.Items(i), Choice)
                If pass = 0 AndAlso c.Cabinet IsNot Nothing AndAlso firstIn3D < 0 Then firstIn3D = i
                If pattern.Length = 0 OrElse Not (pass = 1 OrElse c.Cabinet IsNot Nothing) Then Continue For
                If System.Text.RegularExpressions.Regex.IsMatch(If(c.Text, ""), pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase) Then Return i
            Next
        Next
        Return If(firstIn3D >= 0, firstIn3D, 0)
    End Function

    Private Shared Function CabinetText(c As E3D.Cabinet) As String
        Dim nm = ""
        Dim part = ""
        Try
            nm = c.VisibleName
        Catch
        End Try
        Try
            Dim refs = c.ArticleReferences
            If refs IsNot Nothing AndAlso refs.Length > 0 Then part = refs(0).PartNr
        Catch
        End Try
        Dim space = ""
        Try
            Dim sp = c.InstallationSpace
            If sp IsNot Nothing Then space = "[" & Placer3D.SpaceName(sp) & "] "
        Catch
        End Try
        Return (space & nm & "  " & part).Trim()
    End Function

    ''' <summary>Adds the cabinet parts of the parts database that are not in the list yet.</summary>
    Private Sub AddDbCabinets(sender As Object, e As EventArgs)
        Dim listed As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        For Each o In cboCabinet.Items
            Dim c = DirectCast(o, Choice)
            If Not String.IsNullOrEmpty(c.PartNr) Then listed.Add(c.PartNr)
        Next
        Dim n = 0
        For Each p In _catalog.Where(Function(x) x.Kind = ItemKind.Cabinet AndAlso Not listed.Contains(x.PartNr)).
                               OrderByDescending(Function(x) x.UsedInProject).ThenBy(Function(x) x.SubGroup).ThenBy(Function(x) x.PartNr)
            cboCabinet.Items.Add(New Choice With {.Text = "New from parts DB: " & p.Text & If(p.SubGroup.Length > 0, "  [" & p.SubGroup & "]", ""),
                                                  .PartNr = p.PartNr, .VariantNr = p.VariantNr, .CabName = ""})
            n += 1
        Next
        btnDbCabinets.Enabled = False
        Log(n & " cabinet / enclosure part(s) from the parts database added to the list.")
    End Sub

    Private _surfaces As New List(Of E3D.Placement3D)()
    Private _surfaceCab As E3D.Placement3D

    ''' <summary>
    ''' The surfaces of the chosen cabinet (mounting panels, door(s), side walls, rear wall, roof), every plane of each.
    ''' The one chosen is the one whose size fits the 2D plate (see <see cref="SelectBestSurface"/>).
    ''' </summary>
    Private Sub CabinetChanged(sender As Object, e As EventArgs)
        cboPanel.Items.Clear()
        _surfaces = New List(Of E3D.Placement3D)()
        _surfaceCab = Nothing
        Dim c = TryCast(cboCabinet.SelectedItem, Choice)
        If c Is Nothing Then Return
        Dim isNew = c.Cabinet Is Nothing AndAlso Not c.Loose
        cboSpace.Enabled = isNew
        SelectSpaceFor(c)
        If isNew Then
            cboPanel.Items.Add(New Choice With {.Text = "(the new cabinet's surface that fits the 2D plate: mounting panel, door, side ...)"})
            cboPanel.SelectedIndex = 0
            Return
        End If
        _surfaceCab = c.Cabinet
        _surfaces = If(c.Loose, _loosePanels.ToList(), Placer3D.SurfacesIn(c.Cabinet))
        Dim order As Func(Of E3D.Placement3D, Integer) =
            Function(q As E3D.Placement3D) As Integer
                        Dim r = Placer3D.RoleOf(q, _surfaceCab)
                        If TypeOf q Is E3D.MountingPanel OrElse r = "Panel" Then Return 0
                        If r.StartsWith("Door") Then Return 1
                        Return 2
                    End Function
        _surfaces = _surfaces.OrderBy(order).ThenBy(Function(q) Placer3D.RoleOf(q, _surfaceCab)).ToList()
        For Each sp In _surfaces
            Dim planes As E3D.Plane() = Nothing
            Try
                planes = sp.Planes
            Catch
            End Try
            If planes Is Nothing Then Continue For
            For Each pl In planes
                Dim side = Placer3D.PlaneSideLabel(sp, _surfaceCab, pl)
                cboPanel.Items.Add(New Choice With {.Text = Placer3D.PanelLabel(sp, _surfaceCab) & "  -  " & If(side.Length > 0, side.ToUpperInvariant().Substring(0, 1) & side.Substring(1) & ": ", "") & Placer3D.PlaneText(pl), .Panel = sp, .Plane = pl})
            Next
        Next
        If cboPanel.Items.Count = 0 Then
            cboPanel.Items.Add(New Choice With {.Text = "(this cabinet has no mounting panel or other surface: add one in EPLAN first)"})
        End If
        SelectBestSurface()
    End Sub

    ''' <summary>
    ''' The installation space that goes with the chosen cabinet: for a new cabinet of the project its location
    ''' ("+TD_204-PANEL1" -> TD_204): the existing space with that name, else "New installation space: TD_204" (added
    ''' when it is not in the list); for a cabinet in 3D its own space. A cabinet from the parts database (no device
    ''' tag) keeps the space chosen for the page.
    ''' </summary>
    Private Sub SelectSpaceFor(c As Choice)
        Try
            If c.Cabinet IsNot Nothing Then
                Dim own = c.Cabinet.InstallationSpace
                If own Is Nothing Then Return
                AddSpace(own)
                For k = 0 To cboSpace.Items.Count - 1
                    Dim sc = DirectCast(cboSpace.Items(k), Choice)
                    If sc.Space IsNot Nothing AndAlso sc.Space.Equals(own) Then cboSpace.SelectedIndex = k : Return
                Next
                Return
            End If
            Dim loc = LocationOf(c.CabName)
            If loc.Length = 0 Then Return
            ' an existing space with that name (shown without the prefix signs)
            For k = 0 To cboSpace.Items.Count - 1
                Dim sc = DirectCast(cboSpace.Items(k), Choice)
                If sc.Space Is Nothing Then Continue For
                If String.Equals(sc.Text.TrimStart("="c, "+"c, "#"c), loc, StringComparison.OrdinalIgnoreCase) Then cboSpace.SelectedIndex = k : Return
            Next
            ' else a new one with that name
            For k = 0 To cboSpace.Items.Count - 1
                Dim sc = DirectCast(cboSpace.Items(k), Choice)
                If String.Equals(sc.NewSpaceName, loc, StringComparison.OrdinalIgnoreCase) Then cboSpace.SelectedIndex = k : Return
            Next
            cboSpace.Items.Add(New Choice With {.Text = "New installation space: " & loc, .NewSpaceName = loc})
            cboSpace.SelectedIndex = cboSpace.Items.Count - 1
        Catch
        End Try
    End Sub

    ''' <summary>The location in a device tag: "TD_204" for "+TD_204-PANEL1" or "=A1+TD_204-PANEL1"; "" without one.</summary>
    Private Shared Function LocationOf(tag As String) As String
        If String.IsNullOrEmpty(tag) Then Return ""
        Dim m = System.Text.RegularExpressions.Regex.Match(tag, "\+([^+=\-#&/\s]+)(?!.*\+)")
        Return If(m.Success, m.Groups(1).Value, "")
    End Function

    ''' <summary>Chooses the surface whose size fits the chosen 2D plate (else the front of the mounting panel).</summary>
    Private Sub SelectBestSurface()
        If _surfaces.Count = 0 Then
            If cboPanel.Items.Count > 0 AndAlso cboPanel.SelectedIndex < 0 Then cboPanel.SelectedIndex = 0
            Return
        End If
        Dim plate = TryCast(cboPlate.SelectedItem, Choice)?.Plate
        Dim best = Placer3D.BestSurface(_surfaces, _surfaceCab, plate)
        Dim idx = 0
        If best IsNot Nothing Then
            For k = 0 To cboPanel.Items.Count - 1
                Dim ch = DirectCast(cboPanel.Items(k), Choice)
                If ch.Panel IsNot Nothing AndAlso ch.Panel.Equals(best.Item1) AndAlso ch.Plane IsNot Nothing AndAlso ch.Plane.Equals(best.Item2) Then idx = k : Exit For
            Next
        End If
        cboPanel.SelectedIndex = idx
        UpdateScale()
    End Sub

    ''' <summary>Reads the page and fills the plate list and the grid.</summary>
    Private Sub ReadPage()
        Cursor = Cursors.WaitCursor
        Try
            Dim o As New ReadOptions With {.RailColor = CInt(numRailColor.Value), .ExtendGap = CDbl(numGap.Value),
                                           .RailsFromLines = chkRails.Checked, .DuctsFromRectangles = chkDucts.Checked,
                                           .DrillHoles = chkHoles.Checked}
            _items = LayoutReader.Read(_page, _db, o, _plates)
            Dim existing = LayoutReader.Existing3D(_prj)
            For Each it In _items
                it.AlreadyIn3D = it.Source = ItemSource.Placement AndAlso it.Kind <> ItemKind.Hole AndAlso existing.Contains(it.Key)
            Next
            ' selected objects on the page: only those are ticked (rails and ducts stay ticked)
            If _selected.Count > 0 Then
                For Each it In _items.Where(Function(i) i.Source = ItemSource.Placement)
                    it.Convert = _selected.Any(Function(f) f.Equals(it.Func))
                Next
            End If
            For Each it In _items.Where(Function(i) i.Kind = ItemKind.Skip)
                it.Convert = False
            Next

            cboPlate.Items.Clear()
            Dim devices = _items.Where(Function(i) i.Kind = ItemKind.Device).ToList()
            Dim def = LayoutReader.DefaultPlate(_plates, devices)
            For Each p In _plates
                cboPlate.Items.Add(New Choice With {.Text = String.Format("{0}{1:0.#} x {2:0.#} mm, lower left ({3:0.#}, {4:0.#}){5}",
                                                                          If(String.IsNullOrEmpty(p.PartNr), "", p.PartNr & "  "), p.Width2D, p.Height2D,
                                                                          p.Min2D.X, p.Min2D.Y, If(String.IsNullOrEmpty(p.Name), "", "  " & p.Name)), .Plate = p})
                If p Is def Then cboPlate.SelectedIndex = cboPlate.Items.Count - 1
            Next
            cboPlate.Items.Add(New Choice With {.Text = "(none: the page origin is the lower left of the surface)"})
            If cboPlate.SelectedIndex < 0 Then cboPlate.SelectedIndex = cboPlate.Items.Count - 1

            FillGrid()
            SelectBestSurface()
            Log(String.Format("Page read: {0} devices, {1} rails, {2} ducts, {3} plate candidate(s).",
                              devices.Count, _items.Where(Function(i) i.Kind = ItemKind.Rail).Count(),
                              _items.Where(Function(i) i.Kind = ItemKind.Duct).Count(), _plates.Count))
        Catch ex As Exception
            Log("Reading the page failed: " & ex.Message)
        Finally
            Cursor = Cursors.Default
        End Try
    End Sub

    Private Sub FillGrid()
        _filling = True
        grid.Rows.Clear()
        _choices.Clear()
        For Each it In _items
            Dim i = grid.Rows.Add()
            Dim r = grid.Rows(i)
            r.Tag = it
            r.Cells(colUse.Index).Value = it.Convert
            r.Cells(colType.Index).Value = KindNames(CInt(it.Kind))
            r.Cells(colName.Index).Value = it.Name
            r.Cells(colGroup.Index).Value = it.ProductGroup
            r.Cells(colSize.Index).Value = SizeText(it)
            r.Cells(colIn3D.Index).Value = it.AlreadyIn3D
            r.Cells(colStatus.Index).Value = it.Status
            SetPartCell(r, it)
            Dim tv As String = Nothing
            it.Turn = If(Not String.IsNullOrEmpty(it.PartNr) AndAlso _turns.TryGetValue(it.PartNr, tv), tv, "")
            r.Cells(colTurn.Index).Value = it.Turn
        Next
        _filling = False
        UpdateInfo()
    End Sub

    Private Shared Function SizeText(it As LayoutItem) As String
        If it.Kind = ItemKind.Rail AndAlso it.Source = ItemSource.RailLine Then
            Dim len = Math.Max(it.Width2D, it.Height2D)
            Return String.Format("{0} {1:0.#} (drawn {2:0.#}) @ {3:0.#}, {4:0.#}", If(it.Horizontal, "-", "|"), len, it.DrawnLength, it.CentreX, it.CentreY)
        End If
        Return String.Format("{0:0.#} x {1:0.#} @ {2:0.#}, {3:0.#}", it.Width2D, it.Height2D, it.CentreX, it.CentreY)
    End Function

    ''' <summary>Placements: their own part (text). Rail lines and duct rectangles: the parts from the database to choose from.</summary>
    Private Sub SetPartCell(r As DataGridViewRow, it As LayoutItem)
        If it.Source = ItemSource.Placement Then
            r.Cells(colPart.Index) = New DataGridViewTextBoxCell With {.Value = it.PartNr}
            r.Cells(colPart.Index).ReadOnly = True
            Return
        End If
        Dim list = LayoutReader.Choices(it, _catalog)
        _choices(it) = list
        Dim cell As New DataGridViewComboBoxCell With {.FlatStyle = FlatStyle.Flat}
        For Each p In list
            cell.Items.Add(p.Text)
        Next
        r.Cells(colPart.Index) = cell
        Dim cur = list.FirstOrDefault(Function(pi) pi.PartNr = it.PartNr)
        If cur Is Nothing Then cur = list.FirstOrDefault()
        If cur IsNot Nothing Then
            it.PartNr = cur.PartNr
            it.VariantNr = cur.VariantNr
            cell.Value = cur.Text
            it.ProductGroup = cur.Group
            r.Cells(colGroup.Index).Value = cur.Group
        Else
            it.PartNr = ""
            r.Cells(colStatus.Index).Value = "no " & it.Kind.ToString().ToLowerInvariant() & " in the parts database"
        End If
    End Sub

    Private Sub CellChanged(sender As Object, e As DataGridViewCellEventArgs)
        If _filling OrElse e.RowIndex < 0 Then Return
        Dim r = grid.Rows(e.RowIndex)
        Dim it = TryCast(r.Tag, LayoutItem)
        If it Is Nothing Then Return
        If e.ColumnIndex = colUse.Index Then
            it.Convert = CBool(If(r.Cells(colUse.Index).Value, False))
        ElseIf e.ColumnIndex = colType.Index Then
            Dim k = Array.IndexOf(KindNames, CStr(r.Cells(colType.Index).Value))
            If k >= 0 Then
                it.Kind = CType(k, ItemKind)
                If it.Kind = ItemKind.Skip OrElse it.Kind = ItemKind.Plate Then
                    it.Convert = False
                    _filling = True
                    r.Cells(colUse.Index).Value = False
                    _filling = False
                End If
                If it.Source <> ItemSource.Placement Then
                    _filling = True
                    SetPartCell(r, it)
                    _filling = False
                End If
            End If
        ElseIf e.ColumnIndex = colTurn.Index Then
            ' a turn is for the part: every row with the same part number gets it, and it is remembered
            Dim tv = CStr(If(r.Cells(colTurn.Index).Value, ""))
            If Not String.IsNullOrEmpty(it.PartNr) Then
                If tv.Length = 0 Then _turns.Remove(it.PartNr) Else _turns(it.PartNr) = tv
                _filling = True
                For Each rr As DataGridViewRow In grid.Rows
                    Dim other = TryCast(rr.Tag, LayoutItem)
                    If other Is Nothing OrElse Not String.Equals(other.PartNr, it.PartNr, StringComparison.OrdinalIgnoreCase) Then Continue For
                    other.Turn = tv
                    rr.Cells(colTurn.Index).Value = tv
                Next
                _filling = False
                SaveTurns()
            Else
                it.Turn = tv
            End If
        ElseIf e.ColumnIndex = colPart.Index AndAlso it.Source <> ItemSource.Placement Then
            Dim list As List(Of PartInfo) = Nothing
            If _choices.TryGetValue(it, list) Then
                Dim p = list.FirstOrDefault(Function(x) x.Text = CStr(r.Cells(colPart.Index).Value))
                If p IsNot Nothing Then
                    it.PartNr = p.PartNr
                    it.VariantNr = p.VariantNr
                    it.ProductGroup = p.Group
                    r.Cells(colGroup.Index).Value = p.Group
                End If
            End If
        End If
        UpdateInfo()
    End Sub

    Private Sub UpdateInfo()
        lblInfo.Text = String.Format("{0} of {1} ticked", _items.Where(Function(i) i.Convert).Count(), _items.Count)
    End Sub

    ''' <summary>Scale from the plate: surface width / plate width (shown; used when "from the plate" is ticked).</summary>
    Private Sub UpdateScale()
        numScale.Enabled = Not chkAutoScale.Checked
        If Not chkAutoScale.Checked Then Return
        Dim pc = TryCast(cboPanel.SelectedItem, Choice)
        Dim plate = TryCast(cboPlate.SelectedItem, Choice)?.Plate
        If pc?.Panel Is Nothing OrElse pc.Plane Is Nothing OrElse plate Is Nothing OrElse plate.Width2D <= 0 Then Return
        Dim s = Placer3D.SurfaceOnPlane(pc.Panel, pc.Plane)
        If s.X > 0 Then
            Dim v = CDec(s.X / plate.Width2D)
            If Math.Abs(v - 1D) <= 0.05D Then v = 1D
            If v >= numScale.Minimum AndAlso v <= numScale.Maximum Then numScale.Value = Math.Round(v, 4)
        End If
    End Sub

    ' ---------------------------------------------------------------- files

    ''' <summary>
    ''' Where the settings, the turns and the log are kept: %APPDATA%\CD-PC\ePLAN (the user's roaming application
    ''' data). Created when it is not there. (EPLAN's own program folder lies under Program Files and cannot be
    ''' written: "Access to the path ... is denied".)
    ''' </summary>
    Friend Shared ReadOnly Property DataFolder As String
        Get
            Dim folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CD-PC", "ePLAN")
            Try
                Directory.CreateDirectory(folder)
            Catch
            End Try
            Return folder
        End Get
    End Property

    Private Shared Function DataFile(name As String) As String
        Return Path.Combine(DataFolder, name)
    End Function

    ' ---------------------------------------------------------------- settings

    Private Shared ReadOnly Property SettingsFile As String
        Get
            Return DataFile("PanelLayoutTo3D.settings.cfg")
        End Get
    End Property

    ''' <summary>The "Settings" tab, read from PanelLayoutTo3D.settings.cfg (name=value) next to EPLAN.</summary>
    Private Sub LoadSettings()
        Try
            If Not File.Exists(SettingsFile) Then Return
            Dim d As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            For Each line In File.ReadAllLines(SettingsFile, Encoding.UTF8)
                Dim i = line.IndexOf("="c)
                If i > 0 Then d(line.Substring(0, i).Trim()) = line.Substring(i + 1).Trim()
            Next
            Dim b = Sub(name As String, c As CheckBox)
                        Dim v As String = Nothing
                        If d.TryGetValue(name, v) Then c.Checked = String.Equals(v, "1") OrElse String.Equals(v, "true", StringComparison.OrdinalIgnoreCase)
                    End Sub
            Dim n = Sub(name As String, c As NumericUpDown)
                        Dim v As String = Nothing
                        Dim x As Decimal
                        If d.TryGetValue(name, v) AndAlso Decimal.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, x) Then
                            c.Value = Math.Min(c.Maximum, Math.Max(c.Minimum, x))
                        End If
                    End Sub
            n("Scale", numScale)
            b("ScaleFromPlate", chkAutoScale)
            b("MirrorX", chkFlipX)
            b("MirrorY", chkFlipY)
            n("ShiftRight", numShiftX)
            n("ShiftUp", numShiftY)
            b("RailsFromLines", chkRails)
            n("RailColour", numRailColor)
            n("RailGap", numGap)
            b("DuctsFromRectangles", chkDucts)
            b("SnapToRails", chkSnap)
            b("SkipExisting", chkSkip)
            b("SetNames", chkNames)
            b("DrillHoles", chkHoles)
            n("DrillHoleDiameter", numHoleDia)
            ' window size (not its place: it opens centred on EPLAN)
            Dim w, h As Integer
            Dim sw As String = Nothing, sh As String = Nothing
            If d.TryGetValue("WindowWidth", sw) AndAlso d.TryGetValue("WindowHeight", sh) AndAlso
               Integer.TryParse(sw, NumberStyles.Integer, CultureInfo.InvariantCulture, w) AndAlso
               Integer.TryParse(sh, NumberStyles.Integer, CultureInfo.InvariantCulture, h) Then
                Dim area = Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea
                Size = New Size(Math.Max(MinimumSize.Width, Math.Min(w, area.Width)), Math.Max(MinimumSize.Height, Math.Min(h, area.Height)))
            End If
            Dim mx As String = Nothing
            If d.TryGetValue("WindowMaximized", mx) AndAlso mx = "1" Then WindowState = FormWindowState.Maximized
            Dim s As String = Nothing
            If d.TryGetValue("DuctMake", s) Then _savedDuctMake = s
            If d.TryGetValue("Rail", s) Then _savedRail = s
        Catch ex As Exception
            Log("  Settings not read: " & ex.Message)
        End Try
    End Sub

    Private Sub SaveSettings()
        Try
            Dim inv = CultureInfo.InvariantCulture
            Dim b = Function(c As CheckBox) If(c.Checked, "1", "0")
            ' the normal size, also when maximized or minimized
            Dim winSize = If(WindowState = FormWindowState.Normal, Size, RestoreBounds.Size)
            Dim lines As New List(Of String) From {
                "Scale=" & numScale.Value.ToString(inv),
                "ScaleFromPlate=" & b(chkAutoScale),
                "MirrorX=" & b(chkFlipX),
                "MirrorY=" & b(chkFlipY),
                "ShiftRight=" & numShiftX.Value.ToString(inv),
                "ShiftUp=" & numShiftY.Value.ToString(inv),
                "RailsFromLines=" & b(chkRails),
                "RailColour=" & numRailColor.Value.ToString(inv),
                "RailGap=" & numGap.Value.ToString(inv),
                "DuctsFromRectangles=" & b(chkDucts),
                "SnapToRails=" & b(chkSnap),
                "SkipExisting=" & b(chkSkip),
                "SetNames=" & b(chkNames),
                "DrillHoles=" & b(chkHoles),
                "DrillHoleDiameter=" & numHoleDia.Value.ToString(inv),
                "WindowWidth=" & winSize.Width.ToString(inv),
                "WindowHeight=" & winSize.Height.ToString(inv),
                "WindowMaximized=" & If(WindowState = FormWindowState.Maximized, "1", "0"),
                "DuctMake=" & LayoutReader.PreferredDuctMake,
                "Rail=" & LayoutReader.PreferredRail}
            File.WriteAllLines(SettingsFile, lines, Encoding.UTF8)
        Catch ex As Exception
            Log("Settings not saved to " & SettingsFile & ": " & ex.Message)
        End Try
    End Sub

    ''' <summary>
    ''' The default duct make (the makes of the ducts in the parts database) and default DIN rail (the rails in the
    ''' parts database); the first entry is "the one used most in the project".
    ''' </summary>
    Private Sub FillDefaults()
        _filling = True
        Try
            cboDuctMake.Items.Clear()
            cboDuctMake.Items.Add(MostUsed)
            For Each g In _catalog.Where(Function(p) p.Kind = ItemKind.Duct AndAlso p.Make.Length > 0).
                                   GroupBy(Function(p) p.Make, StringComparer.OrdinalIgnoreCase).OrderBy(Function(x) x.Key)
                cboDuctMake.Items.Add(New Choice With {.Text = String.Format("{0}  ({1} duct{2})", g.Key, g.Count(), If(g.Count() = 1, "", "s")), .PartNr = g.Key})
            Next
            cboDuctMake.SelectedIndex = 0
            For k = 1 To cboDuctMake.Items.Count - 1
                If String.Equals(DirectCast(cboDuctMake.Items(k), Choice).PartNr, _savedDuctMake, StringComparison.OrdinalIgnoreCase) Then cboDuctMake.SelectedIndex = k
            Next

            cboRail.Items.Clear()
            cboRail.Items.Add(MostUsed)
            For Each p In _catalog.Where(Function(x) x.Kind = ItemKind.Rail).OrderByDescending(Function(x) x.UsedInProject).ThenBy(Function(x) x.PartNr)
                cboRail.Items.Add(New Choice With {.Text = p.Text, .PartNr = p.PartNr})
            Next
            cboRail.SelectedIndex = 0
            For k = 1 To cboRail.Items.Count - 1
                If String.Equals(DirectCast(cboRail.Items(k), Choice).PartNr, _savedRail, StringComparison.OrdinalIgnoreCase) Then cboRail.SelectedIndex = k
            Next
            LayoutReader.PreferredDuctMake = If(TryCast(cboDuctMake.SelectedItem, Choice)?.PartNr, "")
            LayoutReader.PreferredRail = If(TryCast(cboRail.SelectedItem, Choice)?.PartNr, "")
            If _savedDuctMake.Length > 0 AndAlso LayoutReader.PreferredDuctMake.Length = 0 Then Log("  Default duct make " & _savedDuctMake & " is not in the parts database.")
            If _savedRail.Length > 0 AndAlso LayoutReader.PreferredRail.Length = 0 Then Log("  Default DIN rail " & _savedRail & " is not in the parts database.")
        Finally
            _filling = False
        End Try
    End Sub

    Private Shared ReadOnly Property MostUsed As Choice
        Get
            Return New Choice With {.Text = "(the one used most in the project)", .PartNr = ""}
        End Get
    End Property

    ''' <summary>Another default: every rail line / duct rectangle row gets the new first choice.</summary>
    Private Sub DefaultsChanged()
        If _filling Then Return
        LayoutReader.PreferredDuctMake = If(TryCast(cboDuctMake.SelectedItem, Choice)?.PartNr, "")
        LayoutReader.PreferredRail = If(TryCast(cboRail.SelectedItem, Choice)?.PartNr, "")
        _savedDuctMake = LayoutReader.PreferredDuctMake
        _savedRail = LayoutReader.PreferredRail
        _filling = True
        Try
            For Each r As DataGridViewRow In grid.Rows
                Dim it = TryCast(r.Tag, LayoutItem)
                If it Is Nothing OrElse it.Source = ItemSource.Placement Then Continue For
                If it.Kind <> ItemKind.Rail AndAlso it.Kind <> ItemKind.Duct Then Continue For
                it.PartNr = ""
                SetPartCell(r, it)
            Next
        Finally
            _filling = False
        End Try
        SaveSettings()
    End Sub

    ' ---------------------------------------------------------------- convert

    Private Sub Convert(sender As Object, e As EventArgs)
        grid.EndEdit()
        SaveSettings()
        Dim cc = TryCast(cboCabinet.SelectedItem, Choice)
        Dim pc = TryCast(cboPanel.SelectedItem, Choice)
        If cc Is Nothing OrElse pc Is Nothing Then Return
        Dim isNew = cc.Cabinet Is Nothing AndAlso Not cc.Loose
        If Not isNew AndAlso pc.Panel Is Nothing Then
            Log("The chosen cabinet has no mounting panel.")
            Return
        End If
        Dim o As New PlaceOptions With {
            .Panel = If(isNew, Nothing, pc.Panel),
            .Plane = If(isNew, Nothing, pc.Plane),
            .NewCabinetPartNr = If(isNew, cc.PartNr, Nothing), .NewCabinetVariant = cc.VariantNr, .NewCabinetName = cc.CabName,
            .Space = TryCast(cboSpace.SelectedItem, Choice)?.Space,
            .NewSpaceName = TryCast(cboSpace.SelectedItem, Choice)?.NewSpaceName, .Page = _page,
            .Plate = TryCast(cboPlate.SelectedItem, Choice)?.Plate,
            .Scale = CDbl(numScale.Value), .AutoScale = chkAutoScale.Checked,
            .FlipX = chkFlipX.Checked, .FlipY = chkFlipY.Checked,
            .ShiftX = CDbl(numShiftX.Value), .ShiftY = CDbl(numShiftY.Value),
            .SnapToRails = chkSnap.Checked, .Catalog = _catalog, .SkipExisting = chkSkip.Checked, .SetNames = chkNames.Checked,
            .HoleDiameter = CDbl(numHoleDia.Value), .Kinds = PlaceKinds()}

        Dim todo = _items.Where(Function(i) i.Convert AndAlso o.Kinds.Contains(i.Kind)).Count()
        If todo = 0 Then
            Log("Nothing ticked (rows and the ""Place"" types on the Standard tab).")
            Return
        End If
        If MessageBox.Show(Me, String.Format("Place {0} object(s) in 3D?", todo), Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) <> DialogResult.OK Then Return

        Cursor = Cursors.WaitCursor
        btnConvert.Enabled = False
        Try
            Log("---- " & DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") & "  page " & SafePageName())
            Dim placer As New Placer3D(_prj, o, AddressOf Log)
            Using New LockingStep()
                Using undo = New Eplan.EplApi.DataModel.UndoManager().CreateUndoStep()
                    undo.SetUndoDescription("2D panel layout -> 3D")
                    placer.Run(_items)
                End Using
            End Using
            ' a new installation space: from now on in the list
            If o.Space IsNot Nothing Then
                AddSpace(o.Space)
                For k = 0 To cboSpace.Items.Count - 1
                    Dim sc = DirectCast(cboSpace.Items(k), Choice)
                    If sc.Space IsNot Nothing AndAlso sc.Space.Equals(o.Space) Then cboSpace.SelectedIndex = k
                Next
            End If
            ' a new cabinet: from now on in the list as a cabinet in 3D (running again places on the same panel)
            If isNew AndAlso o.Panel IsNot Nothing Then
                Dim cab = Placer3D.CabinetOf(o.Panel)
                If cab IsNot Nothing Then
                    cboCabinet.Items.Remove(cc)
                    cboCabinet.Items.Insert(0, New Choice With {.Text = "In 3D: " & CabinetText(cab) & "  (new)", .Cabinet = cab})
                    cboCabinet.SelectedIndex = 0
                End If
            End If
        Catch ex As Exception
            Log("Error: " & ex.Message)
        Finally
            btnConvert.Enabled = True
            Cursor = Cursors.Default
        End Try

        _filling = True
        For Each r As DataGridViewRow In grid.Rows
            Dim it = TryCast(r.Tag, LayoutItem)
            If it Is Nothing Then Continue For
            r.Cells(colStatus.Index).Value = it.Status
            r.Cells(colIn3D.Index).Value = it.AlreadyIn3D
        Next
        _filling = False
        SaveLog()
    End Sub

    ''' <summary>The kinds ticked under "Place" on the Standard tab.</summary>
    Private Function PlaceKinds() As HashSet(Of ItemKind)
        Dim k As New HashSet(Of ItemKind)()
        If chkPlaceDevices.Checked Then k.Add(ItemKind.Device)
        If chkPlaceRails.Checked Then k.Add(ItemKind.Rail)
        If chkPlaceDucts.Checked Then k.Add(ItemKind.Duct)
        If chkPlaceHoles.Checked Then k.Add(ItemKind.Hole)
        Return k
    End Function

    Private Sub Log(s As String)
        _log.AppendLine(s)
        txtLog.AppendText(s & Environment.NewLine)
    End Sub

    ''' <summary>"Save log to file...": the whole log shown in the dialog, to a .log file of the user's choice.</summary>
    Private Sub SaveLogAs(sender As Object, e As EventArgs)
        If txtLog.TextLength = 0 Then
            MessageBox.Show(Me, "The log is empty.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information)
            Return
        End If
        Dim page = SafePageName()
        For Each ch In Path.GetInvalidFileNameChars()
            page = page.Replace(ch, "_"c)
        Next
        Using dlg As New SaveFileDialog With {
            .Title = "Save log to file",
            .Filter = "Log files (*.log)|*.log|All files (*.*)|*.*",
            .DefaultExt = "log",
            .AddExtension = True,
            .OverwritePrompt = True,
            .FileName = "PanelLayoutTo3D " & page & " " & DateTime.Now.ToString("yyyy-MM-dd HH.mm") & ".log",
            .InitialDirectory = DataFolder}
            If dlg.ShowDialog(Me) <> DialogResult.OK Then Return
            Try
                File.WriteAllText(dlg.FileName, txtLog.Text, Encoding.UTF8)
                Log("Log saved to " & dlg.FileName)
            Catch ex As Exception
                MessageBox.Show(Me, "The log could not be saved:" & Environment.NewLine & ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error)
            End Try
        End Using
    End Sub

    Private Shared ReadOnly Property TurnsFile As String
        Get
            Return DataFile("PanelLayoutTo3D.turns.txt")
        End Get
    End Property

    ''' <summary>Part number TAB turn, one per line.</summary>
    Private Sub LoadTurns()
        _turns.Clear()
        Try
            If Not File.Exists(TurnsFile) Then Return
            For Each line In File.ReadAllLines(TurnsFile, Encoding.UTF8)
                Dim parts = line.Split(ControlChars.Tab)
                If parts.Length >= 2 AndAlso parts(0).Trim().Length > 0 AndAlso TurnNames.Contains(parts(1).Trim()) Then _turns(parts(0).Trim()) = parts(1).Trim()
            Next
            If _turns.Count > 0 Then Log(String.Format("  {0} part turn(s) from {1}", _turns.Count, TurnsFile))
        Catch ex As Exception
            Log("  Turns not read: " & ex.Message)
        End Try
    End Sub

    Private Sub SaveTurns()
        Try
            File.WriteAllLines(TurnsFile, _turns.OrderBy(Function(kv) kv.Key).Select(Function(kv) kv.Key & ControlChars.Tab & kv.Value), Encoding.UTF8)
        Catch ex As Exception
            Log("Turns not saved to " & TurnsFile & ": " & ex.Message)
        End Try
    End Sub

    Private Sub SaveLog()
        Try
            File.AppendAllText(DataFile("PanelLayoutTo3D.log"), _log.ToString())
            _log.Clear()
        Catch
        End Try
    End Sub
End Class
