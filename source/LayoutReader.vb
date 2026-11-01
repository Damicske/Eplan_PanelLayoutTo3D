Option Strict On
Option Infer On

Imports System.Globalization
Imports Microsoft.VisualBasic
Imports System.Linq
Imports Eplan.EplApi.Base
Imports Eplan.EplApi.DataModel
Imports Eplan.EplApi.MasterData
Imports EFunction = Eplan.EplApi.DataModel.Function
Imports E3D = Eplan.EplApi.DataModel.E3D
Imports G = Eplan.EplApi.DataModel.Graphics

''' <summary>What a 2D object becomes in 3D.</summary>
Public Enum ItemKind
    Device = 0      ' Component from the part's 3D macro
    Rail = 1        ' MountingRail with the 2D length
    Duct = 2        ' Duct with the 2D length
    Plate = 3       ' the mounting plate on the 2D page: origin (and scale) of the conversion, not placed
    Skip = 4
    Cabinet = 5     ' only for parts in the parts database (not a type in the grid)
    Hole = 6        ' a 2D object without a part on the mounting plate: a drill hole at its middle
End Enum

''' <summary>Where a 2D object comes from.</summary>
Public Enum ItemSource
    Placement       ' a part placement (function) on the page
    RailLine        ' a line in the rail colour (the rail's centre line)
    DuctRectangle   ' a filled rectangle
End Enum

''' <summary>One object on the 2D panel layout page.</summary>
Public Class LayoutItem
    Public Property Source As ItemSource
    Public Property Func As EFunction
    Public Property Name As String = ""
    Public Property PartNr As String = ""
    Public Property VariantNr As String = ""
    ''' <summary>Generic product group / group / sub group of the part in the parts database ("" when not found).</summary>
    Public Property ProductGroup As String = ""
    Public Property Kind As ItemKind
    ''' <summary>Outline on the page (a rail: its centre line, with zero height or width).</summary>
    Public Property Min2D As PointD
    Public Property Max2D As PointD
    ''' <summary>A rail line as drawn, before its ends were moved onto the ducts.</summary>
    Public Property DrawnLength As Double
    Public Property Convert As Boolean = True
    ''' <summary>Extra turn after placing: "", "90", "180", "270" (on the plate) or "flip" (front and back swapped).</summary>
    Public Property Turn As String = ""
    ''' <summary>A placed 3D object with the same device tag and part number exists already.</summary>
    Public Property AlreadyIn3D As Boolean
    Public Property Status As String = ""
    ''' <summary>The 3D object created for it.</summary>
    Public Property Placed As E3D.Placement3D

    Public ReadOnly Property Width2D As Double
        Get
            Return Max2D.X - Min2D.X
        End Get
    End Property

    Public ReadOnly Property Height2D As Double
        Get
            Return Max2D.Y - Min2D.Y
        End Get
    End Property

    Public ReadOnly Property Horizontal As Boolean
        Get
            Return Width2D >= Height2D
        End Get
    End Property

    Public ReadOnly Property CentreX As Double
        Get
            Return (Min2D.X + Max2D.X) / 2
        End Get
    End Property

    Public ReadOnly Property CentreY As Double
        Get
            Return (Min2D.Y + Max2D.Y) / 2
        End Get
    End Property

    Public ReadOnly Property Key As String
        Get
            Return MakeKey(Name, PartNr)
        End Get
    End Property

    Public Shared Function MakeKey(name As String, partNr As String) As String
        Return (If(name, "") & "|" & If(partNr, "")).ToUpperInvariant()
    End Function

    Public Function Contains(x As Double, y As Double, Optional tol As Double = 0) As Boolean
        Return x >= Min2D.X - tol AndAlso x <= Max2D.X + tol AndAlso y >= Min2D.Y - tol AndAlso y <= Max2D.Y + tol
    End Function
End Class

''' <summary>A rail, duct or cabinet part from the parts database.</summary>
Public Class PartInfo
    Public Property PartNr As String = ""
    Public Property VariantNr As String = ""
    Public Property Group As String = ""
    Public Property Kind As ItemKind
    ''' <summary>Width (22013) and height (22014) in mm; 0 when not filled in.</summary>
    Public Property Width As Double
    Public Property Height As Double
    ''' <summary>Depth (ARTICLE_DEPTH) in mm; 0 when not filled in.</summary>
    Public Property Depth As Double
    ''' <summary>How often the part is used in the project (the default is the most used one).</summary>
    Public Property UsedInProject As Integer
    ''' <summary>Description of the part (cabinets).</summary>
    Public Property Description As String = ""
    ''' <summary>Make: the manufacturer in the parts database, else the part number's prefix (RIT, SES, ...).</summary>
    Public Property Make As String = ""

    Public ReadOnly Property Text As String
        Get
            Dim s = PartNr
            If Description.Length > 0 Then s &= "  " & Description
            ' only the sizes filled in in the parts database: "(w 40 x d 60)", "(w 40)", never "40 x 0"
            Dim sizes As New List(Of String)()
            If Width > 0 Then sizes.Add("w " & Width.ToString("0.#", CultureInfo.InvariantCulture))
            If Height > 0 Then sizes.Add("h " & Height.ToString("0.#", CultureInfo.InvariantCulture))
            If Depth > 0 Then sizes.Add("d " & Depth.ToString("0.#", CultureInfo.InvariantCulture))
            If sizes.Count > 0 Then s &= "  (" & String.Join(" x ", sizes) & ")"
            If UsedInProject > 0 Then s &= "  *"
            Return s
        End Get
    End Property

    ''' <summary>The product sub group (last part of <see cref="Group"/>), e.g. "Cabinet", "Enclosure".</summary>
    Public ReadOnly Property SubGroup As String
        Get
            Dim i = If(Group, "").LastIndexOf(" / ", StringComparison.Ordinal)
            Return If(i >= 0, Group.Substring(i + 3), If(Group, ""))
        End Get
    End Property

    Public Overrides Function ToString() As String
        Return Text
    End Function
End Class

''' <summary>Settings for reading the 2D page.</summary>
Public Class ReadOptions
    ''' <summary>Colour index of the rail lines (1 = red).</summary>
    Public RailColor As Integer = 1
    ''' <summary>A rail end within this distance of a duct (or the plate edge) is moved onto it (0 = off).</summary>
    Public ExtendGap As Double = 10
    ''' <summary>Read filled rectangles as ducts.</summary>
    Public DuctsFromRectangles As Boolean = True
    ''' <summary>Read lines in <see cref="RailColor"/> as rails.</summary>
    Public RailsFromLines As Boolean = True
    ''' <summary>2D objects without a part (smaller than a plate) become drill holes in the mounting plate.</summary>
    Public DrillHoles As Boolean
End Class

''' <summary>Reads the objects of a 2D panel layout page and looks their parts up in the parts database.</summary>
Public NotInheritable Class LayoutReader
    Private Sub New()
    End Sub

    Public Shared Function IsPanelLayout(p As Page) As Boolean
        Try
            Return p.PageType.ToString().IndexOf("Panel", StringComparison.OrdinalIgnoreCase) >= 0
        Catch
            Return False
        End Try
    End Function

    ''' <summary>The parts database set in EPLAN's settings, or Nothing.</summary>
    Public Shared Function OpenPartsDatabase() As MDPartsDatabase
        Try
            Dim mgmt As New MDPartsManagement()
            Return mgmt.OpenDatabase()
        Catch
            Return Nothing
        End Try
    End Function

    ''' <summary>
    ''' The page's part placements (devices; also rails, ducts and plates when they are placed as parts), the rail
    ''' lines, the duct rectangles and the candidates for the mounting plate (placements without part, largest
    ''' first). The plate in use is the smallest candidate that holds most of the devices.
    ''' </summary>
    Public Shared Function Read(page As Page, db As MDPartsDatabase, o As ReadOptions, ByRef plates As List(Of LayoutItem)) As List(Of LayoutItem)
        Dim items As New List(Of LayoutItem)()
        plates = New List(Of LayoutItem)()
        Dim groups As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)

        For Each f In page.Functions
            Dim bb = Bounds(f)
            If bb Is Nothing Then Continue For
            Dim mn As New PointD(Math.Min(bb(0).X, bb(1).X), Math.Min(bb(0).Y, bb(1).Y))
            Dim mx As New PointD(Math.Max(bb(0).X, bb(1).X), Math.Max(bb(0).Y, bb(1).Y))
            If mx.X - mn.X < 0.5 AndAlso mx.Y - mn.Y < 0.5 Then Continue For

            Dim refs As ArticleReference() = Nothing
            Try
                refs = f.ArticleReferences
            Catch
            End Try
            Dim pn = If(refs IsNot Nothing AndAlso refs.Length > 0, refs(0).PartNr, "")
            If String.IsNullOrEmpty(pn) Then
                ' no part: the mounting plate (or the enclosure) drawn as a placement
                If (mx.X - mn.X) * (mx.Y - mn.Y) >= 10000 Then
                    plates.Add(New LayoutItem With {.Source = ItemSource.Placement, .Func = f, .Name = SafeName(f), .Kind = ItemKind.Plate,
                                                    .Min2D = mn, .Max2D = mx, .Convert = False})
                ElseIf o.DrillHoles Then
                    ' smaller (the fixing points of rails and ducts drawn without a part): a drill hole at its middle
                    items.Add(New LayoutItem With {.Source = ItemSource.Placement, .Func = f, .Name = SafeName(f), .Kind = ItemKind.Hole,
                                                   .Min2D = mn, .Max2D = mx, .Convert = True, .ProductGroup = "(no part: drill hole)"})
                End If
                Continue For
            End If

            Dim it As New LayoutItem With {
                .Source = ItemSource.Placement, .Func = f, .Name = SafeName(f), .PartNr = pn,
                .VariantNr = If(refs(0).VariantNr, ""), .Min2D = mn, .Max2D = mx}
            Dim g As String = Nothing
            If Not groups.TryGetValue(pn, g) Then
                g = GroupOf(db, pn)
                groups(pn) = g
            End If
            it.ProductGroup = g
            it.Kind = KindFromGroup(g)
            If refs.Length > 1 Then it.Status = refs.Length & " parts, first one used"
            If it.Kind = ItemKind.Plate Then
                it.Convert = False
                plates.Add(it)
            Else
                items.Add(it)
            End If
        Next

        Dim devices = items.Where(Function(i) i.Kind = ItemKind.Device).ToList()

        If o.DuctsFromRectangles OrElse o.RailsFromLines Then
            Dim seen As New HashSet(Of String)()
            For Each gp In page.AllGraphicalPlacements
                If o.DuctsFromRectangles Then
                    Dim r = TryCast(gp, G.Rectangle)
                    If r IsNot Nothing Then
                        Dim d = DuctFromRectangle(r, devices, seen)
                        If d IsNot Nothing Then items.Add(d)
                        Continue For
                    End If
                End If
                If o.RailsFromLines Then
                    Dim l = TryCast(gp, G.Line)
                    If l IsNot Nothing Then
                        Dim rl = RailFromLine(l, o.RailColor, seen)
                        If rl IsNot Nothing Then items.Add(rl)
                    End If
                End If
            Next
        End If

        ' plates: largest first; the default one is the smallest that holds most device centres
        plates = plates.OrderByDescending(Function(p) p.Width2D * p.Height2D).ToList()

        If o.ExtendGap > 0 Then ExtendRails(items, DefaultPlate(plates, devices), o.ExtendGap)
        Return items.OrderBy(Function(i) i.Kind).ThenBy(Function(i) i.Name).ThenBy(Function(i) -i.CentreY).ThenBy(Function(i) i.CentreX).ToList()
    End Function

    ''' <summary>The smallest plate candidate that holds at least half of the devices, else the largest.</summary>
    Public Shared Function DefaultPlate(plates As List(Of LayoutItem), devices As List(Of LayoutItem)) As LayoutItem
        If plates.Count = 0 Then Return Nothing
        If devices.Count = 0 Then Return plates(0)
        Dim best As LayoutItem = Nothing
        For Each p In plates
            Dim n = devices.Where(Function(d) p.Contains(d.CentreX, d.CentreY)).Count()
            If n * 2 >= devices.Count AndAlso (best Is Nothing OrElse p.Width2D * p.Height2D < best.Width2D * best.Height2D) Then best = p
        Next
        Return If(best, plates(0))
    End Function

    Private Shared Function SafeName(f As EFunction) As String
        Try
            Return If(f.Name, "")
        Catch
            Return ""
        End Try
    End Function

    ''' <summary>
    ''' The placement's outline on the page: the function's own (EPLAN keeps the outline of the part's 2D macro on
    ''' the function: insertion point = top left, the part's width and height), else its group.
    ''' </summary>
    Private Shared Function Bounds(f As EFunction) As PointD()
        Try
            Dim b = f.GetBoundingBox()
            If b IsNot Nothing AndAlso b.Length >= 2 AndAlso (Math.Abs(b(1).X - b(0).X) > 0.5 OrElse Math.Abs(b(1).Y - b(0).Y) > 0.5) Then Return b
        Catch
        End Try
        Try
            Dim g = f.Group
            If g IsNot Nothing Then
                Dim b = g.GetBoundingBox()
                If b IsNot Nothing AndAlso b.Length >= 2 Then Return b
            End If
        Catch
        End Try
        Return Nothing
    End Function

    ''' <summary>
    ''' A filled, unturned rectangle at least 15 mm wide and 80 mm long that is not part of a device (26-0266: the
    ''' ducts are grey filled rectangles with an outline on top, the same size as in 3D).
    ''' </summary>
    Private Shared Function DuctFromRectangle(r As G.Rectangle, devices As List(Of LayoutItem), seen As HashSet(Of String)) As LayoutItem
        Try
            If Not r.IsSurfaceFilled Then Return Nothing
            If Math.Abs(r.Angle) > 0.001 Then Return Nothing
            Dim a = r.LowerLeftCorner, b = r.UpperRightCorner
            Dim mn As New PointD(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y))
            Dim mx As New PointD(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y))
            Dim w = mx.X - mn.X, h = mx.Y - mn.Y
            If Math.Min(w, h) < 15 OrElse Math.Max(w, h) < 80 Then Return Nothing
            Dim cx = (mn.X + mx.X) / 2, cy = (mn.Y + mx.Y) / 2
            If devices.Any(Function(d) d.Contains(cx, cy)) Then Return Nothing
            Dim key = String.Format(CultureInfo.InvariantCulture, "R{0:0.0};{1:0.0};{2:0.0};{3:0.0}", mn.X, mn.Y, mx.X, mx.Y)
            If Not seen.Add(key) Then Return Nothing
            Return New LayoutItem With {.Source = ItemSource.DuctRectangle, .Kind = ItemKind.Duct, .Min2D = mn, .Max2D = mx,
                                        .ProductGroup = "(rectangle)"}
        Catch
            Return Nothing
        End Try
    End Function

    ''' <summary>A horizontal or vertical line in the rail colour, at least 30 mm long: the rail's centre line.</summary>
    Private Shared Function RailFromLine(l As G.Line, color As Integer, seen As HashSet(Of String)) As LayoutItem
        Try
            If l.Pen Is Nothing OrElse l.Pen.ColorId <> color Then Return Nothing
            Dim a = l.StartPoint, b = l.EndPoint
            Dim dx = Math.Abs(b.X - a.X), dy = Math.Abs(b.Y - a.Y)
            If Math.Max(dx, dy) < 30 OrElse Math.Min(dx, dy) > 0.5 Then Return Nothing
            Dim mn As New PointD(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y))
            Dim mx As New PointD(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y))
            If dx >= dy Then
                mn = New PointD(mn.X, (a.Y + b.Y) / 2) : mx = New PointD(mx.X, (a.Y + b.Y) / 2)
            Else
                mn = New PointD((a.X + b.X) / 2, mn.Y) : mx = New PointD((a.X + b.X) / 2, mx.Y)
            End If
            Dim key = String.Format(CultureInfo.InvariantCulture, "L{0:0.0};{1:0.0};{2:0.0};{3:0.0}", mn.X, mn.Y, mx.X, mx.Y)
            If Not seen.Add(key) Then Return Nothing
            Return New LayoutItem With {.Source = ItemSource.RailLine, .Kind = ItemKind.Rail, .Min2D = mn, .Max2D = mx,
                                        .DrawnLength = Math.Max(dx, dy), .ProductGroup = "(line)"}
        Catch
            Return Nothing
        End Try
    End Function

    ''' <summary>
    ''' The rail lines stop short of the ducts (26-0266: 5 mm at each end); in the cabinet the rail runs from duct to
    ''' duct. Each end within <paramref name="maxGap"/> of a duct (or of the plate's edge) on the rail's line is moved
    ''' onto it.
    ''' </summary>
    Private Shared Sub ExtendRails(items As List(Of LayoutItem), plate As LayoutItem, maxGap As Double)
        Dim ducts = items.Where(Function(i) i.Kind = ItemKind.Duct).ToList()
        For Each r In items.Where(Function(i) i.Kind = ItemKind.Rail AndAlso i.Source = ItemSource.RailLine)
            If r.Horizontal Then
                Dim y = r.CentreY
                Dim lo = r.Min2D.X, hi = r.Max2D.X
                Dim stops = ducts.Where(Function(d) y >= d.Min2D.Y - 0.5 AndAlso y <= d.Max2D.Y + 0.5).ToList()
                Dim left = stops.Select(Function(d) d.Max2D.X).Where(Function(e) e <= lo + 0.5 AndAlso lo - e <= maxGap).DefaultIfEmpty(Double.NaN).Max()
                Dim right = stops.Select(Function(d) d.Min2D.X).Where(Function(e) e >= hi - 0.5 AndAlso e - hi <= maxGap).DefaultIfEmpty(Double.NaN).Min()
                If Double.IsNaN(left) AndAlso plate IsNot Nothing AndAlso lo - plate.Min2D.X >= 0 AndAlso lo - plate.Min2D.X <= maxGap Then left = plate.Min2D.X
                If Double.IsNaN(right) AndAlso plate IsNot Nothing AndAlso plate.Max2D.X - hi >= 0 AndAlso plate.Max2D.X - hi <= maxGap Then right = plate.Max2D.X
                If Not Double.IsNaN(left) Then lo = left
                If Not Double.IsNaN(right) Then hi = right
                r.Min2D = New PointD(lo, y) : r.Max2D = New PointD(hi, y)
            Else
                Dim x = r.CentreX
                Dim lo = r.Min2D.Y, hi = r.Max2D.Y
                Dim stops = ducts.Where(Function(d) x >= d.Min2D.X - 0.5 AndAlso x <= d.Max2D.X + 0.5).ToList()
                Dim below = stops.Select(Function(d) d.Max2D.Y).Where(Function(e) e <= lo + 0.5 AndAlso lo - e <= maxGap).DefaultIfEmpty(Double.NaN).Max()
                Dim above = stops.Select(Function(d) d.Min2D.Y).Where(Function(e) e >= hi - 0.5 AndAlso e - hi <= maxGap).DefaultIfEmpty(Double.NaN).Min()
                If Double.IsNaN(below) AndAlso plate IsNot Nothing AndAlso lo - plate.Min2D.Y >= 0 AndAlso lo - plate.Min2D.Y <= maxGap Then below = plate.Min2D.Y
                If Double.IsNaN(above) AndAlso plate IsNot Nothing AndAlso plate.Max2D.Y - hi >= 0 AndAlso plate.Max2D.Y - hi <= maxGap Then above = plate.Max2D.Y
                If Not Double.IsNaN(below) Then lo = below
                If Not Double.IsNaN(above) Then hi = above
                r.Min2D = New PointD(x, lo) : r.Max2D = New PointD(x, hi)
            End If
        Next
    End Sub

    ''' <summary>"generic group / group / sub group" of the part as stored in the parts database.</summary>
    Public Shared Function GroupOf(db As MDPartsDatabase, partNr As String) As String
        If db Is Nothing Then Return ""
        Try
            Dim p = db.GetPart(partNr)
            If p Is Nothing Then Return ""
            Return GroupText(p)
        Catch
            Return ""
        End Try
    End Function

    Public Shared Function GroupText(p As MDPart) As String
        Return p.GenericProductGroup.ToString() & " / " & p.ProductGroup.ToString() & " / " & p.ProductSubGroup.ToString()
    End Function

    ''' <summary>Default 3D type from the product group in the parts database.</summary>
    Public Shared Function KindFromGroup(group As String) As ItemKind
        If String.IsNullOrEmpty(group) Then Return ItemKind.Device
        Dim g = group.Replace(" ", "").ToLowerInvariant()
        If g.Contains("mountingpanel") OrElse g.Contains("mountingplate") Then Return ItemKind.Plate
        If g.Contains("duct") OrElse g.Contains("wireway") OrElse g.Contains("trunking") Then Return ItemKind.Duct
        If g.Contains("rail") AndAlso Not g.Contains("busbar") Then Return ItemKind.Rail
        If g.Contains("enclosure") OrElse g.Contains("housing") OrElse g.Contains("door") Then Return ItemKind.Skip
        Return ItemKind.Device
    End Function

    Private Shared _catalog As List(Of PartInfo)
    Private Shared _catalogDb As String

    ''' <summary>
    ''' What a part in the parts database is for the catalog: a cabinet (product group enclosure / cabinet / housing,
    ''' not their accessories or doors), else as <see cref="KindFromGroup"/>.
    ''' </summary>
    Public Shared Function CatalogKind(group As String) As ItemKind
        Dim g = If(group, "").Replace(" ", "").ToLowerInvariant()
        Dim isCab = g.Contains("enclosure") OrElse g.Contains("cabinet") OrElse g.Contains("housing")
        If isCab AndAlso Not IsAccessoryGroup(group) Then Return ItemKind.Cabinet
        Return KindFromGroup(group)
    End Function

    ''' <summary>A product group of parts that go on or in a cabinet (accessories, doors, mounting panels, rails ...).</summary>
    Public Shared Function IsAccessoryGroup(group As String) As Boolean
        Dim g = If(group, "").Replace(" ", "").ToLowerInvariant()
        Return g.Contains("accessor") OrElse g.Contains("extension") OrElse g.Contains("expansion") OrElse g.Contains("door") OrElse
               g.Contains("component") OrElse g.Contains("mountingpanel") OrElse g.Contains("rail") OrElse g.Contains("duct")
    End Function

    ''' <summary>The part's description (first text of a multi-language value), "" when there is none.</summary>
    Private Shared Function Describe(p As MDPart) As String
        For Each name In {"ARTICLE_DESCR1", "ARTICLE_TYPENR"}
            Try
                Dim v = CallByName(p.Properties, name, CallType.Get)
                Dim t = If(v Is Nothing, "", v.ToString())
                t = FirstLanguage(t)
                If t.Length > 0 Then Return If(t.Length > 60, t.Substring(0, 60) & "...", t)
            Catch
            End Try
        Next
        Return ""
    End Function

    ''' <summary>"de_DE@Schaltschrank;nl_NL@Schakelkast;" -> the Dutch, else the English, else the first text.</summary>
    Public Shared Function FirstLanguage(t As String) As String
        If String.IsNullOrEmpty(t) OrElse Not t.Contains("@") Then Return If(t, "").Trim()
        Dim parts = t.Split(";"c).Where(Function(x) x.Contains("@")).Select(Function(x) x.Substring(x.IndexOf("@"c) + 1).Trim()).ToList()
        Dim langs = t.Split(";"c).Where(Function(x) x.Contains("@")).Select(Function(x) x.Substring(0, x.IndexOf("@"c))).ToList()
        For Each want In {"nl_", "en_"}
            For k = 0 To langs.Count - 1
                If langs(k).StartsWith(want, StringComparison.OrdinalIgnoreCase) AndAlso parts(k).Length > 0 Then Return parts(k)
            Next
        Next
        Return If(parts.FirstOrDefault(Function(x) x.Length > 0), "")
    End Function

    ''' <summary>
    ''' The cabinet devices of the project: device tag + part of every article reference whose part is a cabinet in
    ''' the parts database (the cabinet is usually already a device in the project, e.g. -500M1).
    ''' </summary>
    Public Shared Function ProjectCabinets(prj As Project, catalog As List(Of PartInfo)) As List(Of Tuple(Of String, PartInfo))
        Dim result As New List(Of Tuple(Of String, PartInfo))()
        Dim cabs = catalog.Where(Function(c) c.Kind = ItemKind.Cabinet).GroupBy(Function(c) c.PartNr, StringComparer.OrdinalIgnoreCase).ToDictionary(Function(gr) gr.Key, Function(gr) gr.First(), StringComparer.OrdinalIgnoreCase)
        If cabs.Count = 0 Then Return result
        Dim seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Try
            For Each r In New DMObjectsFinder(prj).GetArticleReferences(Nothing)
                Try
                    Dim pn = r.PartNr
                    Dim info As PartInfo = Nothing
                    If String.IsNullOrEmpty(pn) OrElse Not cabs.TryGetValue(pn, info) Then Continue For
                    Dim owner = ""
                    Try
                        Dim o = CallByName(r, "ParentObject", CallType.Get)
                        Dim f = TryCast(o, EFunction)
                        If f IsNot Nothing Then owner = If(f.Name, "")
                    Catch
                    End Try
                    If Not seen.Add(owner & "|" & pn) Then Continue For
                    Dim pi As New PartInfo With {.PartNr = info.PartNr, .VariantNr = If(String.IsNullOrEmpty(r.VariantNr), info.VariantNr, r.VariantNr),
                                                 .Group = info.Group, .Kind = ItemKind.Cabinet, .Description = info.Description,
                                                 .Width = info.Width, .Height = info.Height}
                    result.Add(Tuple.Create(owner, pi))
                Catch
                End Try
            Next
        Catch
        End Try
        Return result.OrderBy(Function(x) x.Item1).ToList()
    End Function

    ''' <summary>
    ''' The rails, ducts and cabinets in the parts database (read once per EPLAN session; a large database
    ''' takes a while). Cabinets: every part in the product group the cabinet parts are in (the whole "Bouwgroep":
    ''' 19 inch, enclosures, mounting hook-up, cabinets), except rails, ducts and mounting panels.
    ''' <paramref name="used"/> = how often each part number is used in the project.
    ''' </summary>
    Public Shared Function Catalog(db As MDPartsDatabase, used As Dictionary(Of String, Integer), Optional reload As Boolean = False) As List(Of PartInfo)
        If db Is Nothing Then Return New List(Of PartInfo)()
        Dim id = ""
        Try
            id = db.ToString()
        Catch
        End Try
        If reload OrElse _catalog Is Nothing OrElse _catalogDb <> id Then
            Dim list As New List(Of PartInfo)()
            ' the whole product group the cabinets are in (Mechanics / "Bouwgroep": 19 inch, enclosures, mounting
            ' hook-up, cabinet ...), not only the cabinet sub group: those parts are looked at after the pass
            Dim cabinetGroups As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            Dim others As New List(Of Tuple(Of MDPart, String, String))()
            For Each p In db.Parts
                Try
                    Dim g = GroupText(p)
                    Dim k = CatalogKind(g)
                    If k = ItemKind.Cabinet Then cabinetGroups.Add(ProductGroupOf(p))
                    If k <> ItemKind.Rail AndAlso k <> ItemKind.Duct AndAlso k <> ItemKind.Cabinet Then
                        If k = ItemKind.Device OrElse k = ItemKind.Skip Then others.Add(Tuple.Create(p, g, ProductGroupOf(p)))
                        Continue For
                    End If
                    list.Add(NewPartInfo(p, g, k))
                Catch
                End Try
            Next
            For Each o In others
                Try
                    If cabinetGroups.Contains(o.Item3) AndAlso Not IsAccessoryGroup(o.Item2) Then list.Add(NewPartInfo(o.Item1, o.Item2, ItemKind.Cabinet))
                Catch
                End Try
            Next
            _catalog = list
            _catalogDb = id
        End If
        For Each p In _catalog
            Dim n = 0
            used.TryGetValue(p.PartNr, n)
            p.UsedInProject = n
        Next
        Return _catalog
    End Function

    ''' <summary>The manufacturer of a part (ARTICLE_MANUFACTURER), else the part number up to its first dot.</summary>
    Private Shared Function MakeOf(p As MDPart) As String
        Try
            Dim v = CallByName(p.Properties, "ARTICLE_MANUFACTURER", CallType.Get)
            Dim s = If(v Is Nothing, "", v.ToString()).Trim()
            If s.Length > 0 Then Return s
        Catch
        End Try
        Return MakeFromPartNr(p.PartNr)
    End Function

    Public Shared Function MakeFromPartNr(partNr As String) As String
        If String.IsNullOrEmpty(partNr) Then Return ""
        Dim i = partNr.IndexOf("."c)
        Return If(i > 0, partNr.Substring(0, i), "")
    End Function

    ''' <summary>Default make for ducts ("" = the one used most in the project), set in the dialog's settings.</summary>
    Public Shared Property PreferredDuctMake As String = ""
    ''' <summary>Default rail part number ("" = the one used most in the project), set in the dialog's settings.</summary>
    Public Shared Property PreferredRail As String = ""

    Private Shared Function NewPartInfo(p As MDPart, g As String, k As ItemKind) As PartInfo
        Return New PartInfo With {.PartNr = p.PartNr, .VariantNr = If(p.Variant, ""), .Group = g, .Kind = k,
                                  .Width = Dimension(p, "ARTICLE_WIDTH"), .Height = Dimension(p, "ARTICLE_HEIGHT"), .Depth = Dimension(p, "ARTICLE_DEPTH"),
                                  .Description = If(k = ItemKind.Cabinet, Describe(p), ""), .Make = MakeOf(p)}
    End Function

    ''' <summary>The generic product group and product group of a part ("Mechanics / Housing"), without the sub group.</summary>
    Private Shared Function ProductGroupOf(p As MDPart) As String
        Return p.GenericProductGroup.ToString() & " / " & p.ProductGroup.ToString()
    End Function

    ''' <summary>A size property of a part (22013 width, 22014 height) in mm, 0 when empty.</summary>
    Private Shared Function Dimension(p As MDPart, name As String) As Double
        Try
            Dim v = CallByName(p.Properties, name, CallType.Get)
            Return ParseMm(If(v Is Nothing, "", v.ToString()))
        Catch
            Return 0
        End Try
    End Function

    Public Shared Function ParseMm(s As String) As Double
        If String.IsNullOrWhiteSpace(s) Then Return 0
        Dim m = System.Text.RegularExpressions.Regex.Match(s, "-?\d+([.,]\d+)?")
        If Not m.Success Then Return 0
        Dim d As Double
        If Double.TryParse(m.Value.Replace(","c, "."c), NumberStyles.Float, CultureInfo.InvariantCulture, d) Then Return d
        Return 0
    End Function

    ''' <summary>How often each part number is used in the project (article references).</summary>
    Public Shared Function PartUsage(prj As Project) As Dictionary(Of String, Integer)
        Dim d As New Dictionary(Of String, Integer)(StringComparer.OrdinalIgnoreCase)
        Try
            For Each r In New DMObjectsFinder(prj).GetArticleReferences(Nothing)
                Try
                    Dim pn = r.PartNr
                    If String.IsNullOrEmpty(pn) Then Continue For
                    Dim n = 0
                    d.TryGetValue(pn, n)
                    d(pn) = n + 1
                Catch
                End Try
            Next
        Catch
        End Try
        Return d
    End Function

    ''' <summary>
    ''' The parts a rail line or duct rectangle can become: rails (any width), ducts as wide as the rectangle (± 1 mm;
    ''' all ducts when none is). The default from the settings first (rail part / duct make), then the most used one
    ''' in the project.
    ''' </summary>
    Public Shared Function Choices(it As LayoutItem, catalog As List(Of PartInfo)) As List(Of PartInfo)
        Dim list As IEnumerable(Of PartInfo)
        If it.Kind = ItemKind.Rail Then
            list = catalog.Where(Function(p) p.Kind = ItemKind.Rail)
        ElseIf it.Kind = ItemKind.Duct Then
            Dim w = Math.Min(it.Width2D, it.Height2D)
            Dim same = catalog.Where(Function(p) p.Kind = ItemKind.Duct AndAlso Math.Abs(p.Width - w) <= 1).ToList()
            list = If(same.Count > 0, same, catalog.Where(Function(p) p.Kind = ItemKind.Duct))
        Else
            Return New List(Of PartInfo)()
        End If
        Dim pref = Function(p As PartInfo) As Integer
                       If it.Kind = ItemKind.Rail Then
                           Return If(PreferredRail.Length > 0 AndAlso String.Equals(p.PartNr, PreferredRail, StringComparison.OrdinalIgnoreCase), 1, 0)
                       End If
                       Return If(PreferredDuctMake.Length > 0 AndAlso String.Equals(p.Make, PreferredDuctMake, StringComparison.OrdinalIgnoreCase), 1, 0)
                   End Function
        Return list.OrderByDescending(pref).ThenByDescending(Function(p) p.UsedInProject).ThenBy(Function(p) If(p.Kind = ItemKind.Duct, p.Depth, p.Height)).ThenBy(Function(p) p.PartNr).ToList()
    End Function

    ''' <summary>Device tag + part number of every placed 3D object in the project.</summary>
    Public Shared Function Existing3D(prj As Project) As HashSet(Of String)
        Dim keys As New HashSet(Of String)()
        Try
            For Each f3 In New DMObjectsFinder(prj).GetPlacements3D(Nothing).OfType(Of E3D.Function3D)()
                Try
                    If Not f3.IsPlaced Then Continue For
                    Dim nm = If(f3.Name, "")
                    Dim refs = f3.ArticleReferences
                    If refs Is Nothing OrElse refs.Length = 0 Then
                        keys.Add(LayoutItem.MakeKey(nm, ""))
                    Else
                        For Each r In refs
                            keys.Add(LayoutItem.MakeKey(nm, r.PartNr))
                        Next
                    End If
                Catch
                End Try
            Next
        Catch
        End Try
        Return keys
    End Function
End Class
