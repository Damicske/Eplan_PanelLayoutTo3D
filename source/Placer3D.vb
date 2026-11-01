Option Strict On
Option Infer On

Imports System.Linq
Imports System.Windows.Media.Media3D
Imports Eplan.EplApi.Base
Imports Eplan.EplApi.DataModel
Imports E3D = Eplan.EplApi.DataModel.E3D

''' <summary>Settings chosen in the dialog.</summary>
Public Class PlaceOptions
    ''' <summary>An existing mounting panel (or other part with planes) in the 3D layout, or Nothing for a new cabinet.</summary>
    Public Panel As E3D.Placement3D
    ''' <summary>The plane of <see cref="Panel"/> to place on (Nothing: its front).</summary>
    Public Plane As E3D.Plane
    ''' <summary>New cabinet from this part (a cabinet device of the project or a part from the parts database).</summary>
    Public NewCabinetPartNr As String
    Public NewCabinetVariant As String
    ''' <summary>Device tag of the new cabinet (the project's cabinet device), "" for none.</summary>
    Public NewCabinetName As String
    Public Space As E3D.InstallationSpace
    ''' <summary>No <see cref="Space"/>: a new installation space with this name.</summary>
    Public NewSpaceName As String
    ''' <summary>The 2D page: a new installation space takes its structure (=, +, ==, ++, #).</summary>
    Public Page As Page
    ''' <summary>The 2D mounting plate: its lower left corner is the lower left corner of the 3D panel's surface.</summary>
    Public Plate As LayoutItem
    Public Scale As Double = 1
    ''' <summary>Scale = 3D surface width / 2D plate width (when there is a 2D plate).</summary>
    Public AutoScale As Boolean = True
    Public FlipX As Boolean
    Public FlipY As Boolean
    ''' <summary>Extra shift on the surface in mm, along page x (to the right) and page y (up).</summary>
    Public ShiftX As Double
    Public ShiftY As Double
    ''' <summary>Devices whose outline lies over a rail go onto that rail (mounting point on the rail's line).</summary>
    Public SnapToRails As Boolean = True
    Public SkipExisting As Boolean = True
    Public SetNames As Boolean = True
    ''' <summary>The rails / ducts of the parts database: replacements for a part EPLAN cannot build.</summary>
    Public Catalog As List(Of PartInfo)
    ''' <summary>Diameter of the drill holes for 2D objects without a part (see <see cref="ItemKind.Hole"/>).</summary>
    Public HoleDiameter As Double = 1
    ''' <summary>Only these kinds are placed this run (the "Place" filter in the dialog); Nothing = all.</summary>
    Public Kinds As HashSet(Of ItemKind)
End Class

''' <summary>
''' Places the 2D objects on a plane of a mounting panel. Everything is worked out in the plane's own coordinates
''' (its AbsoluteTransformation): x / y along the surface from its lower left corner, z out of it (26-0266: the front
''' surface of a mounting panel has its origin in the lower left corner, x to the right, y up, just like the 2D page,
''' and the 2D outline of a part is the size of its 3D part seen from the front). Ducts and rails go on the plane,
''' devices over a rail onto the rail.
''' </summary>
Public Class Placer3D
    Private ReadOnly _prj As Project
    Private ReadOnly _o As PlaceOptions
    Private ReadOnly _log As Action(Of String)

    Private _plane As E3D.Plane
    Private _toWorld As Matrix3D
    Private _toLocal As Matrix3D
    Private _surfMinX, _surfMinY, _surfMaxX, _surfMaxY As Double
    Private _outward As Double = 1
    ' the surface is a mounting panel: everything must end up in front of it
    Private _strictFront As Boolean
    Private _scale As Double = 1
    Private _origin2D As PointD
    ' page x / page y as directions on the plane (each one of ±x, ±y of the plane)
    Private _rx As Double = 1, _ry As Double = 0, _ux As Double = 0, _uy As Double = 1
    ' the part being placed was aligned with the surface (AlignOnPlane): its front faces out
    Private _aligned As Boolean

    Public Sub New(prj As Project, o As PlaceOptions, log As Action(Of String))
        _prj = prj
        _o = o
        _log = log
    End Sub

    ''' <summary>Number of 3D objects created.</summary>
    Public Function Run(items As List(Of LayoutItem)) As Integer
        Dim plate = _o.Plate

        Dim panel = _o.Panel
        If panel Is Nothing Then
            _o.Plane = Nothing
            panel = CreateCabinet(plate)
            If panel Is Nothing Then Return 0
            _o.Panel = panel
        End If
        Dim plane = If(_o.Plane, FrontPlane(panel))
        If plane Is Nothing Then
            _log("The chosen part has no planes.")
            Return 0
        End If
        _log("Plane: " & PlaneText(plane))
        Try
            Dim named = NameCabinetParts(CabinetOf(panel))
            If named > 0 Then _log(named & " part(s) of the cabinet got the cabinet's device tag")
        Catch
        End Try
        ' the layout space in the project's structure (also an existing one whose structure is still empty)
        Try
            Dim sp = If(_o.Space, panel.InstallationSpace)
            If sp IsNot Nothing Then _log("Layout space " & SpaceName(sp) & CopyStructure(sp))
        Catch ex As Exception
            _log("Structure of the layout space not set: " & ex.Message)
        End Try

        SetSurface(panel, plane)
        _log(String.Format("Surface {0:0.#} x {1:0.#} mm (width x height)", SurfWidth, SurfHeight))

        ' objects placed by an earlier run of this dialog that were undone since: not placed any more
        For Each it In items
            If it.Placed IsNot Nothing AndAlso Not Alive(it.Placed) Then
                it.Placed = Nothing
                it.AlreadyIn3D = False
            End If
        Next

        ' a 2D door outline over two (or more) doors side by side: each object onto the door it lies on
        Dim doors = DoorsBeside(panel, plate)
        If doors IsNot Nothing Then
            Dim total = RunDoors(items, plate, doors)
            _log(total & " object(s) placed.")
            Return total
        End If

        _scale = _o.Scale
        If plate IsNot Nothing Then
            _origin2D = plate.Min2D
            If plate.Width2D > 0 AndAlso plate.Height2D > 0 Then
                Dim sx = SurfWidth / plate.Width2D, sy = SurfHeight / plate.Height2D
                ' the 2D page is in real mm: a plate within 5 % of the surface keeps scale 1 (a door outline of
                ' 999 x 1808 on a door face of 992 x 1784.5, +APS_201/9); only a page drawn to another scale is scaled
                If _o.AutoScale Then _scale = If(Math.Abs(sx - 1) <= 0.05, 1.0, sx)
                _log(String.Format("2D plate {0:0.#} x {1:0.#} at ({2:0.#}, {3:0.#}): scale x {4:0.####} / y {5:0.####}, used {6:0.####}",
                                   plate.Width2D, plate.Height2D, plate.Min2D.X, plate.Min2D.Y, sx, sy, _scale))
                If Math.Abs(sx - sy) > 0.03 * Math.Max(sx, sy) Then
                    _log("  The 2D plate and the 3D surface do not have the same proportions: wrong plane or plate?")
                End If
            End If
        Else
            _origin2D = New PointD(0, 0)
            _log(String.Format("No 2D plate: page origin = lower left of the surface, scale {0:0.####}", _scale))
        End If

        Dim n = PlaceAll(items, plate, Nothing)
        _log(n & " object(s) placed.")
        Return n
    End Function

    ''' <summary>
    ''' Works on this surface from now on: the plane's coordinates, the surface on it, which side is out, how the
    ''' page lies on it, and whether it is a mounting panel.
    ''' </summary>
    Private Sub SetSurface(panel As E3D.Placement3D, plane As E3D.Plane)
        _plane = plane
        _strictFront = TypeOf panel Is E3D.MountingPanel OrElse RoleOf(panel, CabinetOf(panel)) = "Panel"
        _toWorld = _plane.AbsoluteTransformation
        _toLocal = _toWorld
        _toLocal.Invert()
        ' the surface on the plane: its mounting surface from the plane's origin, else the panel's outline
        Dim pb = LocalBox(panel, False)
        Dim ms = SurfaceSize(_plane)
        If ms.X > 1 AndAlso ms.Y > 1 Then
            _surfMinX = 0 : _surfMaxX = ms.X
            _surfMinY = 0 : _surfMaxY = ms.Y
        Else
            _surfMinX = pb.X : _surfMaxX = pb.X + pb.SizeX
            _surfMinY = pb.Y : _surfMaxY = pb.Y + pb.SizeY
        End If
        ' the panel lies behind its front plane: the side away from the panel is the front
        _outward = If(pb.Z + pb.SizeZ / 2 <= 0, 1.0, -1.0)
        OrientPage()
    End Sub

    ''' <summary>Places the ticked objects (of <paramref name="only"/>, when given) on the current surface.</summary>
    Private Function PlaceAll(items As List(Of LayoutItem), plate As LayoutItem, only As HashSet(Of LayoutItem)) As Integer
        Dim n = 0
        For Each kind In {ItemKind.Duct, ItemKind.Rail, ItemKind.Device, ItemKind.Hole}
            If _o.Kinds IsNot Nothing AndAlso Not _o.Kinds.Contains(kind) Then Continue For
            ' devices: large ones first, so a carrier (the S7-1500 mounting rail) is there, and turned, before the
            ' modules that go onto it
            For Each it In items.Where(Function(i) i.Convert AndAlso i.Kind = kind AndAlso (only Is Nothing OrElse only.Contains(i))).
                                 OrderByDescending(Function(i) If(kind = ItemKind.Device, i.Width2D * i.Height2D, 0.0))
                If plate IsNot Nothing AndAlso Not plate.Contains(it.CentreX, it.CentreY, 10) Then
                    it.Status = "outside the 2D plate, skipped"
                    Continue For
                End If
                If kind = ItemKind.Hole Then
                    If PlaceHole(it) Then n += 1
                    Continue For
                End If
                If _o.SkipExisting AndAlso it.AlreadyIn3D AndAlso it.Source = ItemSource.Placement Then
                    it.Status = "already in 3D, skipped"
                    Continue For
                End If
                ' a rail line / duct rectangle placed by an earlier run (e.g. ducts and rails first, devices after)
                If _o.SkipExisting AndAlso it.Source <> ItemSource.Placement AndAlso it.Placed IsNot Nothing Then
                    it.Status = "placed by an earlier run, skipped"
                    Continue For
                End If
                If String.IsNullOrEmpty(it.PartNr) Then
                    it.Status = "no part chosen"
                    Continue For
                End If
                If PlaceItem(it, items) Then n += 1
            Next
        Next
        Return n
    End Function

    ''' <summary>A door (or other surface) beside the chosen one, with its rectangle seen from the front.</summary>
    Private Class SideSurface
        Public Panel As E3D.Placement3D
        Public Plane As E3D.Plane
        ''' <summary>The surface's extent along page right (R) and page up (U), in mm (world).</summary>
        Public MinR, MaxR, MinU, MaxU As Double
    End Class

    ''' <summary>
    ''' The chosen surface and the surfaces of the same kind beside it in the cabinet (the other door of a two-door
    ''' cabinet), each with its plane facing the same way at the same depth, when the 2D plate spans all of them and
    ''' not the chosen one alone (+TD_203/9: a 1199 x 2008 door outline over two doors of 592 x 1984.5). Nothing
    ''' otherwise.
    ''' </summary>
    Private Function DoorsBeside(panel As E3D.Placement3D, plate As LayoutItem) As List(Of SideSurface)
        If plate Is Nothing OrElse plate.Width2D <= 0 OrElse plate.Height2D <= 0 Then Return Nothing
        If TypeOf panel Is E3D.MountingPanel Then Return Nothing
        ' the plate fits the chosen surface: nothing to split
        If plate.Width2D <= SurfWidth * 1.3 AndAlso plate.Height2D <= SurfHeight * 1.3 Then Return Nothing
        Try
            Dim cab = CabinetOf(panel)
            If cab Is Nothing Then Return Nothing
            Dim role = RoleOf(panel, cab, False)
            Dim n0 = _toWorld.Transform(New Vector3D(0, 0, 1))
            n0.Normalize()
            Dim o0 = _toWorld.Transform(New Point3D(0, 0, 0))
            Dim rightW = _toWorld.Transform(New Vector3D(_rx, _ry, 0))
            Dim upW = _toWorld.Transform(New Vector3D(_ux, _uy, 0))
            rightW.Normalize() : upW.Normalize()

            Dim list As New List(Of SideSurface) From {Extent(panel, _plane, rightW, upW)}
            For Each p In SurfacesIn(cab)
                If p Is panel OrElse p.Equals(panel) OrElse TypeOf p Is E3D.MountingPanel Then Continue For
                If RoleOf(p, cab, False) <> role Then Continue For
                Dim best As E3D.Plane = Nothing
                Dim bestDepth = 30.0
                Dim planes As E3D.Plane() = Nothing
                Try
                    planes = p.Planes
                Catch
                End Try
                If planes Is Nothing Then Continue For
                For Each pl In planes
                    Try
                        Dim m = pl.AbsoluteTransformation
                        Dim z = m.Transform(New Vector3D(0, 0, 1))
                        z.Normalize()
                        If Vector3D.DotProduct(z, n0) < 0.99 Then Continue For
                        Dim depth = Math.Abs(Vector3D.DotProduct(m.Transform(New Point3D(0, 0, 0)) - o0, n0))
                        If depth < bestDepth Then bestDepth = depth : best = pl
                    Catch
                    End Try
                Next
                If best IsNot Nothing Then list.Add(Extent(p, best, rightW, upW))
            Next
            If list.Count < 2 Then Return Nothing
            ' the plate must cover them together (10 %)
            Dim w = list.Max(Function(d) d.MaxR) - list.Min(Function(d) d.MinR)
            Dim h = list.Max(Function(d) d.MaxU) - list.Min(Function(d) d.MinU)
            If Math.Abs(w - plate.Width2D) > 0.1 * plate.Width2D OrElse Math.Abs(h - plate.Height2D) > 0.1 * plate.Height2D Then
                _log(String.Format("  {0} surfaces side by side ({1:0.#} x {2:0.#} mm together) do not match the 2D plate", list.Count, w, h))
                Return Nothing
            End If
            Return list.OrderBy(Function(d) d.MinR).ThenBy(Function(d) d.MinU).ToList()
        Catch ex As Exception
            _log("  Surfaces beside the chosen one not read: " & ex.Message)
            Return Nothing
        End Try
    End Function

    ''' <summary>The surface on this plane (as <see cref="SetSurface"/> takes it) seen from the front.</summary>
    Private Shared Function Extent(panel As E3D.Placement3D, plane As E3D.Plane, rightW As Vector3D, upW As Vector3D) As SideSurface
        Dim m = plane.AbsoluteTransformation
        Dim minX = 0.0, minY = 0.0, maxX = 0.0, maxY = 0.0
        Dim ms = SurfaceSize(plane)
        If ms.X > 1 AndAlso ms.Y > 1 Then
            maxX = ms.X : maxY = ms.Y
        Else
            Dim inv = m
            inv.Invert()
            Dim b = ToLocal(panel.GetBoundingBox(False), inv)
            minX = b.X : maxX = b.X + b.SizeX
            minY = b.Y : maxY = b.Y + b.SizeY
        End If
        Dim s As New SideSurface With {.Panel = panel, .Plane = plane, .MinR = Double.MaxValue, .MaxR = Double.MinValue, .MinU = Double.MaxValue, .MaxU = Double.MinValue}
        For Each c In {New Point3D(minX, minY, 0), New Point3D(maxX, minY, 0), New Point3D(minX, maxY, 0), New Point3D(maxX, maxY, 0)}
            Dim w = m.Transform(c)
            Dim v As New Vector3D(w.X, w.Y, w.Z)
            Dim r = Vector3D.DotProduct(v, rightW), u = Vector3D.DotProduct(v, upW)
            s.MinR = Math.Min(s.MinR, r) : s.MaxR = Math.Max(s.MaxR, r)
            s.MinU = Math.Min(s.MinU, u) : s.MaxU = Math.Max(s.MaxU, u)
        Next
        Return s
    End Function

    ''' <summary>
    ''' The 2D plate over several surfaces side by side: its lower left corner is the lower left corner of them
    ''' together, each object goes onto the surface its middle lies on (else the nearest one), at its place on the page.
    ''' </summary>
    Private Function RunDoors(items As List(Of LayoutItem), plate As LayoutItem, doors As List(Of SideSurface)) As Integer
        Dim r0 = doors.Min(Function(d) d.MinR), r1 = doors.Max(Function(d) d.MaxR)
        Dim u0 = doors.Min(Function(d) d.MinU), u1 = doors.Max(Function(d) d.MaxU)
        Dim sx = (r1 - r0) / plate.Width2D
        Dim s = _o.Scale
        If _o.AutoScale Then s = If(Math.Abs(sx - 1) <= 0.05, 1.0, sx)
        _log(String.Format("{0} surfaces side by side, {1:0.#} x {2:0.#} mm together; 2D plate {3:0.#} x {4:0.#} at ({5:0.#}, {6:0.#}), scale {7:0.####}",
                           doors.Count, r1 - r0, u1 - u0, plate.Width2D, plate.Height2D, plate.Min2D.X, plate.Min2D.Y, s))
        ' which surface each object lies on
        Dim sets = doors.Select(Function(d) New HashSet(Of LayoutItem)()).ToList()
        For Each it In items.Where(Function(i) i.Convert)
            Dim r = r0 + (it.CentreX - plate.Min2D.X) * s + _o.ShiftX
            Dim u = u0 + (it.CentreY - plate.Min2D.Y) * s + _o.ShiftY
            Dim bestK = 0
            Dim bestD = Double.MaxValue
            For k = 0 To doors.Count - 1
                Dim d = doors(k)
                Dim dr = Math.Max(0, Math.Max(d.MinR - r, r - d.MaxR))
                Dim du = Math.Max(0, Math.Max(d.MinU - u, u - d.MaxU))
                Dim dist = dr + du
                If dist < bestD Then bestD = dist : bestK = k
            Next
            sets(bestK).Add(it)
        Next
        Dim total = 0
        For k = 0 To doors.Count - 1
            If sets(k).Count = 0 Then Continue For
            Dim d = doors(k)
            _log(String.Format("-- surface {0} of {1}: {2}, {3} object(s)", k + 1, doors.Count, PanelLabel(d.Panel, CabinetOf(d.Panel)), sets(k).Count))
            SetSurface(d.Panel, d.Plane)
            _scale = s
            _origin2D = New PointD(plate.Min2D.X + (d.MinR - r0) / s, plate.Min2D.Y + (d.MinU - u0) / s)
            total += PlaceAll(items, plate, sets(k))
        Next
        Return total
    End Function

    ''' <summary>Still in the 3D layout (not undone or deleted since it was placed).</summary>
    Private Shared Function Alive(p As E3D.Placement3D) As Boolean
        Try
            Return p.IsValid AndAlso p.IsPlaced
        Catch
            Return False
        End Try
    End Function

    ''' <summary>
    ''' Creates the cabinet from its part (the part's 3D macro: body, doors, mounting panel ...) in the installation
    ''' space, beside what is there already, gives it the device tag and returns its mounting panel.
    ''' </summary>
    Private Function CreateCabinet(plate As LayoutItem) As E3D.Placement3D
        If String.IsNullOrEmpty(_o.NewCabinetPartNr) Then
            _log("No cabinet chosen.")
            Return Nothing
        End If
        If _o.Space Is Nothing AndAlso Not String.IsNullOrEmpty(_o.NewSpaceName) Then
            Try
                Dim sp As New E3D.InstallationSpace()
                sp.Create(_prj, _o.NewSpaceName)
                _o.Space = sp
                _log("New installation space " & _o.NewSpaceName)
            Catch ex As Exception
                _log("Installation space " & _o.NewSpaceName & " could not be created: " & ex.Message)
                Return Nothing
            End Try
        End If
        If _o.Space Is Nothing Then
            _log("Choose an installation space for the new cabinet.")
            Return Nothing
        End If
        ' the right side of what is in the space already
        Dim right = Double.NaN
        Try
            Dim sb = _o.Space.GetBoundingBox(True)
            If Not sb.IsEmpty AndAlso sb.SizeX > 0 Then right = sb.X + sb.SizeX
        Catch
        End Try

        Dim cab As E3D.Cabinet = Nothing
        Dim extra As New List(Of E3D.Placement3D)()
        Dim lastError = ""
        For Each macroVariant In {0, -2}
            Try
                cab = New E3D.Cabinet()
                cab.Create(_prj, _o.NewCabinetPartNr, _o.NewCabinetVariant, macroVariant, extra)
                Exit For
            Catch ex As Exception
                lastError = ex.Message
                cab = Nothing
            End Try
        Next
        If cab Is Nothing Then
            _log("Cabinet " & _o.NewCabinetPartNr & " could not be created (" & lastError & "). Does the part have a 3D macro?")
            Return Nothing
        End If
        cab.SetParent(_o.Space, False)
        If Not String.IsNullOrEmpty(_o.NewCabinetName) Then
            Try
                cab.Name = _o.NewCabinetName
            Catch ex As Exception
                _log("  Device tag " & _o.NewCabinetName & " not set: " & ex.Message)
            End Try
        End If
        If Not Double.IsNaN(right) Then
            Try
                Dim cb = cab.GetBoundingBox(True)
                If Not cb.IsEmpty Then
                    Dim m = cab.AbsoluteTransformation
                    m.Translate(New Vector3D(right + 300 - cb.X, 0, 0))
                    cab.AbsoluteTransformation = m
                End If
            Catch
            End Try
        End If
        _log("New cabinet " & _o.NewCabinetName & " " & _o.NewCabinetPartNr & " in " & SpaceName(_o.Space))

        Dim surfaces = SurfacesIn(cab)
        For Each x In extra
            If Not surfaces.Contains(x) AndAlso IsSurfacePart(x) Then surfaces.Add(x)
        Next
        Dim pick = BestSurface(surfaces, cab, plate)
        If pick Is Nothing OrElse pick.Item2 Is Nothing Then
            _log("  The cabinet's macro has no mounting panel or other surface. Add one in EPLAN, then run again with this cabinet.")
            Return Nothing
        End If
        _o.Plane = pick.Item2
        _log("  Surface: " & PanelLabel(pick.Item1, cab))
        Return pick.Item1
    End Function

    ''' <summary>
    ''' Puts the installation space in the project's structure: its name parts (Function3D.NameParts, the same way
    ''' EPLAN names a device) get the page's identifiers (functional assignment ==, plant =, place of installation ++,
    ''' location +, installation number #). 26-0266 stores a layout space like that: name "APS_201" (36452) and
    ''' location APS_201 (1200), linked to the structure identifier; the sample project "B1.X1" = +B1.X1. Setting the
    ''' location property on its own is refused by EPLAN (S063113). A value comes from the page's own identifier, else
    ''' from the page name ("+APS_201/8"). Returns a note for the log.
    ''' </summary>
    Private Function CopyStructure(sp As E3D.InstallationSpace) As String
        If _o.Page Is Nothing OrElse sp Is Nothing Then Return ""
        Dim want = PageStructureParts()
        If want.Count = 0 Then Return " (no structure on the page)"

        Dim parts As FunctionBasePropertyList = Nothing
        Try
            parts = sp.NameParts
        Catch
        End Try
        If parts Is Nothing Then parts = DirectCast(Activator.CreateInstance(GetType(FunctionBasePropertyList)), FunctionBasePropertyList)

        Dim changed = False
        Dim notes As New List(Of String)()
        For Each kv In want
            Dim pv = PartOf(parts, kv.Key)
            If pv Is Nothing Then Continue For
            Dim cur = ""
            Try
                If Not pv.IsEmpty Then cur = pv.ToString()
            Catch
            End Try
            If cur = kv.Value Then
                notes.Add(kv.Key & kv.Value & " (was set)")
                Continue For
            End If
            Try
                pv.Set(kv.Value)
                changed = True
                notes.Add(kv.Key & kv.Value)
            Catch ex As Exception
                notes.Add(kv.Key & kv.Value & " (" & ex.Message & ")")
            End Try
        Next
        If changed Then
            Try
                sp.NameParts = parts
            Catch ex As Exception
                Return ", structure NOT set: " & ex.Message & " (" & String.Join(" ", notes) & ")"
            End Try
        End If
        ' read back
        Dim now = ""
        Try
            now = SpaceName(sp)
            Dim np = sp.NameParts
            Dim loc = PartOf(np, "+")
            If loc IsNot Nothing AndAlso Not loc.IsEmpty Then now &= " (+" & loc.ToString() & ")"
        Catch
        End Try
        Return ", structure " & String.Join(" ", notes) & If(now.Length > 0, " -> " & now, "")
    End Function

    ''' <summary>The name part for a prefix ("+" -> DESIGNATION_LOCATION ...).</summary>
    Private Shared Function PartOf(parts As FunctionBasePropertyList, prefix As String) As PropertyValue
        If parts Is Nothing Then Return Nothing
        Select Case prefix
            Case "==" : Return parts.DESIGNATION_FUNCTIONALASSIGNMENT
            Case "=" : Return parts.DESIGNATION_PLANT
            Case "++" : Return parts.DESIGNATION_PLACEOFINSTALLATION
            Case "+" : Return parts.DESIGNATION_LOCATION
            Case "#" : Return parts.DESIGNATION_INSTALLATIONNUMBER
        End Select
        Return Nothing
    End Function

    ''' <summary>The page's structure identifiers by prefix: from the page's properties, else from its name.</summary>
    Private Function PageStructureParts() As Dictionary(Of String, String)
        Dim d As New Dictionary(Of String, String)()
        Dim fromName = ParseStructure(SafeName(_o.Page))
        Dim pp = _o.Page.Properties
        Dim take = Sub(prefix As String, getter As Func(Of PropertyValue))
                       Dim v = ""
                       Try
                           Dim pv = getter()
                           If pv IsNot Nothing AndAlso Not pv.IsEmpty Then v = pv.ToString()
                       Catch
                       End Try
                       If String.IsNullOrEmpty(v) Then fromName.TryGetValue(prefix, v)
                       If Not String.IsNullOrEmpty(v) Then d(prefix) = v
                   End Sub
        take("==", Function() pp.DESIGNATION_FUNCTIONALASSIGNMENT)
        take("=", Function() pp.DESIGNATION_PLANT)
        take("++", Function() pp.DESIGNATION_PLACEOFINSTALLATION)
        take("+", Function() pp.DESIGNATION_LOCATION)
        take("#", Function() pp.DESIGNATION_INSTALLATIONNUMBER)
        Return d
    End Function

    Private Shared Function SafeName(p As Page) As String
        Try
            Return If(p.Name, "")
        Catch
            Return ""
        End Try
    End Function

    ''' <summary>"==F=P++I+L#N&amp;D/12" -> {"==": F, "=": P, "++": I, "+": L, "#": N} (the page part after "/" left out).</summary>
    Public Shared Function ParseStructure(pageName As String) As Dictionary(Of String, String)
        Dim d As New Dictionary(Of String, String)()
        If String.IsNullOrEmpty(pageName) Then Return d
        Dim k = pageName.LastIndexOf("/"c)
        Dim s = If(k >= 0, pageName.Substring(0, k), pageName)
        For Each m As System.Text.RegularExpressions.Match In System.Text.RegularExpressions.Regex.Matches(s, "(==|=|\+\+|\+|#|&)([^=+#&]*)")
            Dim pre = m.Groups(1).Value
            If pre <> "&" AndAlso Not d.ContainsKey(pre) Then d(pre) = m.Groups(2).Value.Trim()
        Next
        Return d
    End Function

    Public Shared Function SpaceName(sp As E3D.InstallationSpace) As String
        Try
            Return If(sp.VisibleName, sp.Name)
        Catch
            Return "?"
        End Try
    End Function

    ''' <summary>
    ''' The parts of a cabinet that things are mounted on, all levels below it: mounting panels, doors, side walls,
    ''' rear wall, roof ... (a MountingPanel, or a flat part with planes: thinner than 60 mm, at least 250 x 250).
    ''' </summary>
    Public Shared Function SurfacesIn(root As E3D.Placement3D) As List(Of E3D.Placement3D)
        Dim list As New List(Of E3D.Placement3D)()
        Dim stack As New Stack(Of E3D.Placement3D)()
        stack.Push(root)
        Dim guard = 0
        While stack.Count > 0 AndAlso guard < 100000
            guard += 1
            Dim p = stack.Pop()
            Dim kids As E3D.Placement3D() = Nothing
            Try
                kids = p.Children
            Catch
            End Try
            If kids Is Nothing Then Continue While
            For Each k In kids
                If IsSurfacePart(k) Then list.Add(k)
                stack.Push(k)
            Next
        End While
        Return list
    End Function

    Public Shared Function IsSurfacePart(p As E3D.Placement3D) As Boolean
        Try
            If TypeOf p Is E3D.MountingPanel Then Return True
            If TypeOf p Is E3D.Plane Then Return False
            Dim planes = p.Planes
            If planes Is Nothing OrElse planes.Length = 0 Then Return False
            Dim b = p.GetBoundingBox(False)
            Dim d = {b.SizeX, b.SizeY, b.SizeZ}.OrderBy(Function(v) v).ToArray()
            Return d(0) <= 60 AndAlso d(1) >= 250
        Catch
            Return False
        End Try
    End Function

    Private Shared Function Area(p As E3D.Placement3D) As Double
        Try
            Dim b = p.GetBoundingBox(False)
            Dim d = {b.SizeX, b.SizeY, b.SizeZ}.OrderByDescending(Function(v) v).ToArray()
            Return d(0) * d(1)
        Catch
            Return 0
        End Try
    End Function

    ''' <summary>
    ''' What the part is in its cabinet, from its type and where it lies (EPLAN's front view looks along +Y): "Mounting
    ''' panel", "Door", "Side wall, left", "Rear wall", "Roof" ... The thin side of the part says which way it faces.
    ''' </summary>
    Public Shared Function RoleOf(p As E3D.Placement3D, cab As E3D.Placement3D, Optional withSide As Boolean = True) As String
        If TypeOf p Is E3D.MountingPanel Then Return "Mounting panel"
        Dim isDoor = p.GetType().Name.IndexOf("Door", StringComparison.OrdinalIgnoreCase) >= 0
        Try
            Dim b = p.GetBoundingBox(False)
            Dim thin = If(b.SizeX <= b.SizeY AndAlso b.SizeX <= b.SizeZ, 0, If(b.SizeY <= b.SizeZ, 1, 2))
            Dim off = 0.0
            If cab IsNot Nothing Then
                Dim cb = cab.GetBoundingBox(True)
                Dim pc = {b.X + b.SizeX / 2, b.Y + b.SizeY / 2, b.Z + b.SizeZ / 2}
                Dim cc = {cb.X + cb.SizeX / 2, cb.Y + cb.SizeY / 2, cb.Z + cb.SizeZ / 2}
                Dim half = {Math.Max(cb.SizeX / 2, 1), Math.Max(cb.SizeY / 2, 1), Math.Max(cb.SizeZ / 2, 1)}
                off = (pc(thin) - cc(thin)) / half(thin)
            End If
            Select Case thin
                Case 1
                    If isDoor OrElse off < -0.6 Then Return "Door" & If(withSide, DoorSide(p, cab, off), "")
                    If off > 0.6 Then Return "Rear wall"
                    Return "Panel"
                Case 0
                    Return If(isDoor, "Side door, ", "Side wall, ") & If(off < 0, "left", "right")
                Case Else
                    Return If(off > 0, "Roof", "Bottom / gland plate")
            End Select
        Catch
            Return If(isDoor, "Door", "Panel")
        End Try
    End Function

    ''' <summary>
    ''' The plane things go on by default: on a mounting panel its front, on the other parts the side facing away
    ''' from the middle of the cabinet (the outside of a door or side wall).
    ''' </summary>
    Public Shared Function DefaultPlane(p As E3D.Placement3D, cab As E3D.Placement3D) As E3D.Plane
        If cab Is Nothing OrElse TypeOf p Is E3D.MountingPanel Then Return FrontPlane(p)
        Dim planes As E3D.Plane() = Nothing
        Try
            planes = p.Planes
        Catch
        End Try
        If planes Is Nothing OrElse planes.Length = 0 Then Return Nothing
        If RoleOf(p, cab) = "Panel" Then Return FrontPlane(p)
        Try
            Dim cb = cab.GetBoundingBox(True)
            Dim centre As New Point3D(cb.X + cb.SizeX / 2, cb.Y + cb.SizeY / 2, cb.Z + cb.SizeZ / 2)
            Return planes.OrderByDescending(Function(pl) Facing(pl, centre)).First()
        Catch
            Return planes(0)
        End Try
    End Function

    ''' <summary>How much the plane faces away from a point (its z axis . (its origin - point)).</summary>
    ''' <summary>
    ''' A label per plane of a door, wall or roof: "outside" (facing away from the middle of the cabinet), "inside"
    ''' (facing into it), and for a second plane facing the same way that lies deeper: "inside, mounting area 20 mm in"
    ''' (26-0266 / VX25 door: outside 992 x 1784.5, inside of the skin 988 x 1780.5, the mounting area on the door's
    ''' frame 895 x 1670 about 20 mm further in). Planes have no name of their own. Empty for a mounting panel.
    ''' </summary>
    Public Shared Function PlaneSideLabel(part As E3D.Placement3D, cab As E3D.Placement3D, pl As E3D.Plane) As String
        If cab Is Nothing OrElse TypeOf part Is E3D.MountingPanel Then Return ""
        Try
            If RoleOf(part, cab) = "Panel" Then Return ""
            Dim cb = cab.GetBoundingBox(True)
            Dim centre As New Point3D(cb.X + cb.SizeX / 2, cb.Y + cb.SizeY / 2, cb.Z + cb.SizeZ / 2)
            Dim m = pl.AbsoluteTransformation
            Dim n = m.Transform(New Vector3D(0, 0, 1))
            n.Normalize()
            Dim o = m.Transform(New Point3D(0, 0, 0))
            Dim outside = Vector3D.DotProduct(n, o - centre) > 0
            ' the other planes of the part facing the same way: how much deeper this one lies than the outermost
            Dim d = Vector3D.DotProduct(n, CType(o, Vector3D))
            Dim deeper = 0.0
            For Each other In part.Planes
                Dim mo = other.AbsoluteTransformation
                Dim no = mo.Transform(New Vector3D(0, 0, 1))
                no.Normalize()
                If Vector3D.DotProduct(no, n) < 0.99 Then Continue For
                Dim dOther = Vector3D.DotProduct(n, CType(mo.Transform(New Point3D(0, 0, 0)), Vector3D))
                ' outside: the outermost has the largest d; inside (n points to the middle): the smallest
                Dim diff = If(outside, dOther - d, d - dOther)
                If diff > deeper Then deeper = diff
            Next
            Dim label = If(outside, "outside", "inside")
            If deeper > 1 Then label &= String.Format(", mounting area {0:0} mm in", deeper)
            Return label
        Catch
            Return ""
        End Try
    End Function

    Private Shared Function Facing(pl As E3D.Plane, centre As Point3D) As Double
        Try
            Dim m = pl.AbsoluteTransformation
            Dim z = m.Transform(New Vector3D(0, 0, 1))
            z.Normalize()
            Dim o = m.Transform(New Point3D(0, 0, 0))
            Return Vector3D.DotProduct(z, o - centre)
        Catch
            Return 0
        End Try
    End Function

    ''' <summary>
    ''' The surface that fits the 2D page: the plane whose size (width x height seen from the front) is closest to the
    ''' 2D plate's outline (within 15 %): the mounting panel for the panel layout, the door for a door layout ... On a
    ''' tie the default plane of the part wins. Without a fitting one: the front of the first mounting panel.
    ''' </summary>
    Public Shared Function BestSurface(parts As List(Of E3D.Placement3D), cab As E3D.Placement3D, plate As LayoutItem) As Tuple(Of E3D.Placement3D, E3D.Plane)
        Dim best As Tuple(Of E3D.Placement3D, E3D.Plane) = Nothing
        Dim bestScore = Double.MaxValue
        If plate IsNot Nothing AndAlso plate.Width2D > 0 AndAlso plate.Height2D > 0 Then
            For Each p In parts
                Dim def = DefaultPlane(p, cab)
                Dim planes As E3D.Plane() = Nothing
                Try
                    planes = p.Planes
                Catch
                End Try
                If planes Is Nothing Then Continue For
                For Each pl In planes
                    Dim s = SurfaceOnPlane(p, pl)
                    Dim score = Math.Abs(s.X - plate.Width2D) + Math.Abs(s.Y - plate.Height2D)
                    If def IsNot Nothing AndAlso Not pl.Equals(def) Then score += 1
                    If score < bestScore Then bestScore = score : best = Tuple.Create(p, pl)
                Next
            Next
            If best IsNot Nothing AndAlso bestScore <= 0.15 * (plate.Width2D + plate.Height2D) Then Return best
        End If
        Dim mp = parts.Where(Function(q) TypeOf q Is E3D.MountingPanel).OrderByDescending(Function(q) Area(q)).FirstOrDefault()
        If mp Is Nothing Then mp = parts.Where(Function(q) RoleOf(q, cab) = "Panel").OrderByDescending(Function(q) Area(q)).FirstOrDefault()
        If mp Is Nothing Then mp = parts.FirstOrDefault()
        If mp Is Nothing Then Return Nothing
        Return Tuple.Create(mp, DefaultPlane(mp, cab))
    End Function

    ''' <summary>"Mounting panel SE.NSYMM1210  1099 x 1896 x 3 mm".</summary>
    Public Shared Function PanelLabel(p As E3D.Placement3D, Optional cab As E3D.Placement3D = Nothing) As String
        Dim nm = ""
        Dim part = ""
        Dim f3 = TryCast(p, E3D.Function3D)
        If f3 IsNot Nothing Then
            Try
                nm = f3.VisibleName
            Catch
            End Try
            Try
                Dim refs = f3.ArticleReferences
                If refs IsNot Nothing AndAlso refs.Length > 0 Then part = refs(0).PartNr
            Catch
            End Try
        End If
        Dim size = ""
        Try
            Dim b = p.GetBoundingBox(False)
            Dim d = {b.SizeX, b.SizeY, b.SizeZ}.OrderByDescending(Function(v) v).ToArray()
            size = String.Format("{0:0} x {1:0} x {2:0} mm", d(0), d(1), d(2))
        Catch
        End Try
        Dim kind = If(cab IsNot Nothing, RoleOf(p, cab), If(TypeOf p Is E3D.MountingPanel, "Mounting panel", p.GetType().Name))
        Return (kind & " " & nm & " " & part).Trim() & "  " & size
    End Function

    ''' <summary>
    ''' Which of the cabinet's front doors this is, seen from the front: " left" / " right" for two doors,
    ''' " 1 (from the left)" ... for more, "" for one. Seen from the front (outside the doors, Z up), right is +X for
    ''' doors on the -Y side of the cabinet (EPLAN's front view), -X for doors on the other side.
    ''' </summary>
    Private Shared Function DoorSide(p As E3D.Placement3D, cab As E3D.Placement3D, off As Double) As String
        If cab Is Nothing Then Return ""
        Try
            Dim doors = SurfacesIn(cab).Where(Function(q) RoleOf(q, cab, False) = "Door").ToList()
            If Not doors.Any(Function(q) q Is p OrElse q.Equals(p)) Then doors.Add(p)
            If doors.Count <= 1 Then Return ""
            Dim sign = If(off <= 0, 1.0, -1.0)
            Dim centreX = Function(q As E3D.Placement3D) As Double
                              Dim b = q.GetBoundingBox(False)
                              Return sign * (b.X + b.SizeX / 2)
                          End Function
            Dim ordered = doors.OrderBy(centreX).ToList()
            Dim k = ordered.FindIndex(Function(q) q Is p OrElse q.Equals(p))
            If k < 0 Then Return ""
            If doors.Count = 2 Then Return If(k = 0, " left", " right")
            Return " " & (k + 1).ToString() & " (from the left)"
        Catch
            Return ""
        End Try
    End Function

    ''' <summary>The cabinet a placement is in (its parents), or Nothing.</summary>
    Public Shared Function CabinetOf(p As E3D.Placement3D) As E3D.Cabinet
        Dim q = p
        Dim guard = 0
        While q IsNot Nothing AndAlso guard < 200
            guard += 1
            Dim c = TryCast(q, E3D.Cabinet)
            If c IsNot Nothing Then Return c
            Try
                q = q.Parent
            Catch
                Return Nothing
            End Try
        End While
        Return Nothing
    End Function

    ''' <summary>
    ''' The front plane: one called front (Front / Vorderseite / voorzijde / avant), else the one facing the front of
    ''' the layout space (EPLAN's front view looks along +Y: the face whose z axis points to -Y), else the largest.
    ''' </summary>
    Public Shared Function FrontPlane(panel As E3D.Placement3D) As E3D.Plane
        Dim planes As E3D.Plane() = Nothing
        Try
            planes = panel.Planes
        Catch
        End Try
        If planes Is Nothing OrElse planes.Length = 0 Then Return Nothing
        For Each p In planes
            Dim t = PlaneName(p).ToLowerInvariant()
            If t.Contains("front") OrElse t.Contains("vorder") OrElse t.Contains("voor") OrElse t.Contains("avant") Then Return p
        Next
        Return planes.OrderBy(Function(pl) Math.Round(ZAxis(pl).Y, 2)).ThenByDescending(Function(pl) SurfaceSize(pl).X * SurfaceSize(pl).Y).First()
    End Function

    Private Shared Function ZAxis(p As E3D.Plane) As Vector3D
        Try
            Dim v = p.AbsoluteTransformation.Transform(New Vector3D(0, 0, 1))
            v.Normalize()
            Return v
        Catch
            Return New Vector3D(0, 0, 0)
        End Try
    End Function

    ''' <summary>
    ''' The plane's name as the layout space navigator shows it ("S1:Deur buiten", "S1:Deur binnen",
    ''' "S1:Profielniveau deur", "S1:Montageplaat achterzijde"), without the device tag in front. A plane stores no
    ''' name of its own (Name / VisibleName give only the structure "+"; the zw1 data only has the side code 10004 ...):
    ''' EPLAN makes the text from the macro, which ToString() returns. Else the description property, else "".
    ''' </summary>
    Public Shared Function PlaneName(p As E3D.Plane) As String
        Dim candidates As New List(Of String)()
        Try
            candidates.Add(p.ToString())
        Catch
        End Try
        For Each prop In {"FUNCTION3D_DESCRIPTION", "DESIGNATION_DESCRIPTION"}
            Try
                Dim v = CallByName(p.Properties, prop, CallType.Get)
                If v IsNot Nothing Then candidates.Add(LayoutReader.FirstLanguage(v.ToString()))
            Catch
            End Try
        Next
        Try
            candidates.Add(p.VisibleName)
        Catch
        End Try
        For Each c In candidates
            Dim t = If(c, "").Trim()
            If t.Length = 0 OrElse t.StartsWith("Eplan.", StringComparison.Ordinal) Then Continue For
            ' "S1:Deur buiten" -> "Deur buiten" (the part before the last ':' is the cabinet's device tag)
            Dim k = t.LastIndexOf(":"c)
            If k >= 0 AndAlso k < t.Length - 1 Then t = t.Substring(k + 1).Trim()
            If Not IsEmptyName(t) Then Return t
        Next
        Return ""
    End Function

    Public Shared Function SurfaceSize(p As E3D.Plane) As PointD
        Try
            Dim s = p.MountingSurface
            Return New PointD(Math.Abs(s.X), Math.Abs(s.Y))
        Catch
            Return New PointD(0, 0)
        End Try
    End Function

    Public Shared Function PlaneText(p As E3D.Plane) As String
        Dim n = PlaneName(p)
        ' a plane has no name of its own: only the structure ("+") shows
        If IsEmptyName(n) Then n = "Plane"
        Dim s = SurfaceSize(p)
        Dim z = ZAxis(p)
        Dim facing = If(z.Y < -0.7, "front", If(z.Y > 0.7, "back", If(z.Z > 0.7, "top", If(z.Z < -0.7, "bottom", If(z.X > 0.7, "right", If(z.X < -0.7, "left", "?"))))))
        Return String.Format("{0} ({1:0.#} x {2:0.#} mm, facing {3})", n, s.X, s.Y, facing)
    End Function

    ''' <summary>
    ''' A 2D object without a part on the mounting plate (the fixing points of rails and ducts drawn as macros without
    ''' a part): a round drilling of <see cref="PlaceOptions.HoleDiameter"/> mm in the mounting panel at the middle of
    ''' its 2D outline. Only on a mounting panel; not twice at the same spot (re-running).
    ''' </summary>
    Private Function PlaceHole(it As LayoutItem) As Boolean
        If Not _strictFront Then
            it.Status = "drill hole: not on a mounting panel, skipped"
            _log(Label(it) & ": " & it.Status)
            Return False
        End If
        Dim t = TargetRect(it)
        Dim x = (t.Item1 + t.Item3) / 2, y = (t.Item2 + t.Item4) / 2
        Dim dia = If(_o.HoleDiameter > 0, _o.HoleDiameter, 1.0)
        Try
            If HoleAt(x, y) Then
                it.Status = "drill hole already there, skipped"
                it.AlreadyIn3D = True
                _log(Label(it) & ": " & it.Status)
                Return False
            End If
            Dim d = E3D.Drilling.CreateRound(_prj, dia, Nothing)
            Dim how = ""
            Dim pm As E3D.PlaneMate = Nothing
            Try
                pm = _plane.BaseMate
            Catch
            End Try
            Dim src As E3D.PointMate = Nothing
            Try
                Dim arr = d.GetSourceMates(E3D.Mate.Enums.PlacementOptions.None)
                If arr IsNot Nothing Then
                    Dim pts = arr.Cast(Of E3D.Mate)().OfType(Of E3D.PointMate)().ToList()
                    src = If(pts.FirstOrDefault(Function(m) m.IsBaseMate), pts.FirstOrDefault())
                End If
            Catch
            End Try
            If pm IsNot Nothing AndAlso src IsNot Nothing Then
                ' snapped onto the surface as by hand, then onto the spot
                Try
                    src.SnapTo(pm, 0.0, 0.0, 0.0)
                    Dim p0 = HoleLocal(d)
                    src.SnapTo(pm, 0.0, x - p0.X, y - p0.Y)
                    Dim p1 = HoleLocal(d)
                    If Math.Abs(x - p1.X) > 0.05 OrElse Math.Abs(y - p1.Y) > 0.05 Then MoveLocal(d, x - p1.X, y - p1.Y, 0)
                    how = "snapped onto the surface"
                Catch ex As Exception
                    how = ""
                End Try
            End If
            If how.Length = 0 Then
                ' no mate: hung on the surface's plane, its axis along the surface's normal
                Dim planeObj = TryCast(CObj(_plane), E3D.Placement3D)
                If planeObj IsNot Nothing Then d.Parent = planeObj
                Dim m = _toWorld
                Dim p = _toWorld.Transform(New Point3D(x, y, 0))
                m.OffsetX = p.X : m.OffsetY = p.Y : m.OffsetZ = p.Z
                d.AbsoluteTransformation = m
                how = "placed on the surface's plane"
            End If
            it.Placed = d
            it.AlreadyIn3D = True
            it.Status = String.Format(System.Globalization.CultureInfo.InvariantCulture, "drill hole Ø{0:0.##} mm, {1}", dia, how)
            _log(Label(it) & ": " & it.Status)
            Return True
        Catch ex As Exception
            it.Status = "drill hole: " & ex.Message
            _log(Label(it) & ": " & it.Status)
            Return False
        End Try
    End Function

    ''' <summary>
    ''' Right after a point of the part was snapped onto the plane mate (offset 0, 0): that point lies on the plane
    ''' mate's origin, so its place in the part is that origin seen from the part. Nothing when that origin is not on
    ''' the part (then the reading is not trusted).
    ''' </summary>
    Private Function SnappedPoint(dev As E3D.Placement3D, pm As E3D.PlaneMate) As Point3D?
        Try
            Dim w = _toWorld.Transform(New Point3D(0, 0, 0))
            Try
                Dim t = pm.Transformation
                For Each c In {Matrix3D.Multiply(t, _toWorld).Transform(New Point3D(0, 0, 0)), t.Transform(New Point3D(0, 0, 0))}
                    Dim q = _toLocal.Transform(c)
                    If Math.Abs(q.Z) < 1 AndAlso q.X >= _surfMinX - 50 AndAlso q.X <= _surfMaxX + 50 AndAlso q.Y >= _surfMinY - 50 AndAlso q.Y <= _surfMaxY + 50 Then
                        w = c
                        Exit For
                    End If
                Next
            Catch
            End Try
            Dim box = dev.GetBoundingBox(True)
            If box.IsEmpty Then Return Nothing
            If w.X < box.X - 25 OrElse w.X > box.X + box.SizeX + 25 OrElse w.Y < box.Y - 25 OrElse w.Y > box.Y + box.SizeY + 25 OrElse
               w.Z < box.Z - 25 OrElse w.Z > box.Z + box.SizeZ + 25 Then Return Nothing
            Dim aInv = dev.AbsoluteTransformation
            If Not aInv.HasInverse Then Return Nothing
            aInv.Invert()
            Return aInv.Transform(w)
        Catch
            Return Nothing
        End Try
    End Function

    ''' <summary>A drilling's position in the plane's coordinates.</summary>
    Private Function HoleLocal(d As E3D.Placement3D) As Point3D
        Dim m = d.AbsoluteTransformation
        Return _toLocal.Transform(New Point3D(m.OffsetX, m.OffsetY, m.OffsetZ))
    End Function

    ''' <summary>A drilling already within 0.5 mm of this spot on the surface (in the panel or its planes).</summary>
    Private Function HoleAt(x As Double, y As Double) As Boolean
        Dim pl = TryCast(CObj(_plane), E3D.Placement3D)
        If pl Is Nothing Then Return False
        Dim root As E3D.Placement3D = If(pl.Parent, pl)
        Dim stack As New Stack(Of E3D.Placement3D)()
        stack.Push(root)
        Dim seen = 0
        While stack.Count > 0 AndAlso seen < 20000
            Dim p = stack.Pop()
            seen += 1
            If TypeOf p Is E3D.Drilling Then
                Dim q = HoleLocal(p)
                If Math.Abs(q.X - x) <= 0.5 AndAlso Math.Abs(q.Y - y) <= 0.5 AndAlso Math.Abs(q.Z) <= 5 Then Return True
            End If
            Dim kids As E3D.Placement3D() = Nothing
            Try
                kids = p.Children
            Catch
            End Try
            If kids IsNot Nothing Then
                For Each c In kids
                    If c IsNot Nothing Then stack.Push(c)
                Next
            End If
        End While
        Return False
    End Function

    Private Function PlaceItem(it As LayoutItem, items As List(Of LayoutItem)) As Boolean
        _aligned = False
        Try
            Dim obj = CreateObject(it)
            If obj Is Nothing Then
                it.Status = "not created (no 3D macro for this part?)"
                _log(Label(it) & ": " & it.Status)
                Return False
            End If
            it.Placed = obj
            Dim note = ""
            If _o.SetNames AndAlso Not String.IsNullOrEmpty(it.Name) Then
                Dim f3 = TryCast(obj, E3D.Function3D)
                If f3 IsNot Nothing Then
                    Try
                        f3.Name = it.Name
                    Catch ex As Exception
                        note = ", device tag not set: " & ex.Message
                    End Try
                End If
            End If
            ' EPLAN snaps it (devices over a rail onto the rail, the rest onto the surface); without mounting points
            ' (no 3D macro) or when snapping fails it is placed by its bounding box
            Dim how = ""
            Dim rail As LayoutItem = Nothing
            If it.Kind = ItemKind.Device AndAlso _o.SnapToRails Then rail = RailUnder(it, items)
            Dim mateName = ""
            ' rails and ducts are not snapped: placed by their outline (hung on the surface, turned 90 degrees when the
            ' 2D outline runs the other way, middle on the 2D outline, back on the surface) as in the first versions.
            ' Once EPLAN has snapped a duct its direction stays as the snap left it: the vertical ducts at the plate's
            ' sides came out lying across, whatever angle was given.
            Dim size3D As String
            If it.Kind = ItemKind.Rail OrElse it.Kind = ItemKind.Duct Then
                size3D = Position(obj, it)
                it.Status = "placed, 3D " & size3D & ", placed by its outline" & ApplyTurn(obj, it) & note
                it.AlreadyIn3D = True
                _log(Label(it) & ": " & it.Status)
                Return True
            End If
            ' onto a device with a mounting line of its own (the S7-1500 mounting rail) first
            Dim carrierLine As E3D.LineMate = Nothing
            Dim carrierOwn As E3D.PointMate = Nothing
            Dim carrier = If(it.Kind = ItemKind.Device AndAlso _o.SnapToRails, CarrierUnder(obj, it, items, carrierLine, carrierOwn), Nothing)
            Dim snapped As Boolean
            If carrier IsNot Nothing AndAlso SnapOnCarrier(obj, it, carrier, carrierLine, carrierOwn, how) Then
                snapped = True
                mateName = "#own"
            Else
                snapped = If(rail IsNot Nothing, SnapOnRail(obj, it, rail, how, mateName), SnapOnPlane(obj, it, how, mateName))
            End If
            If snapped Then
                ' snapped with its own point (as by hand): left as EPLAN put it
                If mateName <> "#own" Then how &= SettleOnTarget(obj, it, rail, mateName)
                size3D = SizeText(obj, it)
            Else
                Dim why = how
                size3D = Position(obj, it)
                how = ""
                If it.Kind = ItemKind.Device AndAlso _o.SnapToRails Then how = SnapToRail(obj, it, items)
                If it.Kind = ItemKind.Device AndAlso how.Length = 0 Then how = SnapToSurface(obj)
                how &= If(why.Length > 0, why, ", no mounting point: placed by its outline")
            End If
            ' on a door or side wall: the body goes into the cabinet
            ' on a mounting panel: back on the panel
            ' (not after aligning with the surface: its front is out already; turning it round put the HMI on
            ' +TD_203/9 backwards)
            If it.Kind = ItemKind.Device AndAlso rail Is Nothing AndAlso carrier Is Nothing Then how &= If(_aligned, "", FaceOut(obj, it)) & BackOnSurface(obj, it, items)
            how &= ApplyTurn(obj, it)
            it.Status = "placed, 3D " & size3D & how & note
            it.AlreadyIn3D = True
            _log(Label(it) & ": " & it.Status)
            Return True
        Catch ex As Exception
            it.Status = "error: " & ex.Message
            _log(Label(it) & ": " & it.Status)
            Return False
        End Try
    End Function

    Private Shared Function Label(it As LayoutItem) As String
        Dim s = If(String.IsNullOrEmpty(it.Name), it.Kind.ToString().ToLowerInvariant(), it.Name)
        Return s & " " & it.PartNr & String.Format(" @ {0:0.#}, {1:0.#}", it.CentreX, it.CentreY)
    End Function

    ' parts that EPLAN could not build as a rail / duct -> the part used instead (for the rest of the run)
    Private ReadOnly _substitute As New Dictionary(Of String, PartInfo)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _cannot As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

    ''' <summary>
    ''' Creates the 3D object. A rail or duct part that EPLAN cannot build ("MountingRail-object kan niet worden
    ''' gegenereerd": a part without a rail / duct 3D macro, RIT.5001082) is replaced by the next fitting part from the
    ''' parts database (rails by use in the project, ducts as wide as the rectangle); the replacement is kept for the
    ''' rest of the run and noted in the status.
    ''' </summary>
    Private Function CreateObject(it As LayoutItem) As E3D.Placement3D
        If it.Kind <> ItemKind.Rail AndAlso it.Kind <> ItemKind.Duct Then
            ' -2: the first variant of the part's 3D macro; else variant A; else the part without a macro variant
            Dim c As E3D.Component = Nothing
            Dim firstError As Exception = Nothing
            Try
                c = E3D.Component.Create(_prj, _plane, it.PartNr, it.VariantNr, -2, Nothing)
            Catch ex As Exception
                firstError = ex
            End Try
            If c Is Nothing Then
                Try
                    c = E3D.Component.Create(_prj, _plane, it.PartNr, it.VariantNr, 0, Nothing)
                Catch
                End Try
            End If
            If c Is Nothing Then
                Try
                    Dim c2 As New E3D.Component()
                    c2.Create(_prj, it.PartNr, it.VariantNr)
                    c = c2
                Catch
                End Try
            End If
            If c Is Nothing Then
                Throw New InvalidOperationException("EPLAN cannot build this part in 3D (no 3D macro / no dimensions in the parts database)" &
                                                    If(firstError Is Nothing, "", ": " & firstError.Message))
            End If
            If Not c.IsPlaced Then c.SetParent(_plane, False)
            Return c
        End If
        Dim sub0 As PartInfo = Nothing
        If _substitute.TryGetValue(it.PartNr, sub0) Then
            it.Status = "(" & it.PartNr & " cannot be built, " & sub0.PartNr & " used)"
            it.PartNr = sub0.PartNr : it.VariantNr = sub0.VariantNr
        End If
        Dim tries As New List(Of PartInfo)()
        tries.Add(New PartInfo With {.PartNr = it.PartNr, .VariantNr = it.VariantNr})
        If _o.Catalog IsNot Nothing Then
            tries.AddRange(LayoutReader.Choices(it, _o.Catalog).Where(Function(p) Not String.Equals(p.PartNr, it.PartNr, StringComparison.OrdinalIgnoreCase)))
        End If
        Dim original = it.PartNr
        Dim lastError As Exception = Nothing
        For Each p In tries
            If _cannot.Contains(p.PartNr) Then Continue For
            Try
                Dim obj = BuildLong(it.Kind, p.PartNr, p.VariantNr, Math.Max(it.Width2D, it.Height2D) * _scale)
                If Not String.Equals(p.PartNr, original, StringComparison.OrdinalIgnoreCase) Then
                    _substitute(original) = p
                    it.Status = "(" & original & " cannot be built, " & p.PartNr & " used)"
                    _log(String.Format("  {0} {1} cannot be built as a {2}; {3} used instead", it.Kind.ToString().ToLowerInvariant(), original,
                                       If(it.Kind = ItemKind.Rail, "mounting rail", "duct"), p.PartNr))
                    it.PartNr = p.PartNr : it.VariantNr = p.VariantNr
                End If
                Return obj
            Catch ex As Exception
                _cannot.Add(p.PartNr)
                lastError = ex
            End Try
        Next
        If lastError IsNot Nothing Then Throw lastError
        Return Nothing
    End Function

    Private Function BuildLong(kind As ItemKind, partNr As String, variantNr As String, length As Double) As E3D.Placement3D
        If kind = ItemKind.Rail Then
            Dim r As New E3D.MountingRail()
            r.Create(_prj, partNr, variantNr, length)
            r.SetParent(_plane, False)
            Return r
        End If
        Dim d As New E3D.Duct()
        d.Create(_prj, partNr, variantNr, length)
        d.SetParent(_plane, False)
        Return d
    End Function

    ''' <summary>
    ''' Turns the object 90° on the plane when it lies the other way than in 2D, moves its middle onto the middle of
    ''' the 2D outline (a rail: onto its line) and its back onto the plane. Returns the 3D size ("45 x 90 x 75").
    ''' </summary>
    Private Function Position(obj As E3D.Placement3D, it As LayoutItem) As String
        Dim t = TargetRect(it)
        Dim tW = t.Item3 - t.Item1, tH = t.Item4 - t.Item2

        Dim turnedHow = ""
        If it.Kind = ItemKind.Rail OrElse it.Kind = ItemKind.Duct Then turnedHow = LayLong(obj, it)
        Dim box = LocalBox(obj, True)
        If box.IsEmpty Then Return "(no size)"
        Dim turn As Boolean
        If it.Kind = ItemKind.Rail OrElse it.Kind = ItemKind.Duct Then
            turn = False
        Else
            Dim keep = Math.Abs(tW - box.SizeX) + Math.Abs(tH - box.SizeY)
            Dim swap = Math.Abs(tW - box.SizeY) + Math.Abs(tH - box.SizeX)
            turn = swap + 0.5 < keep
        End If
        If turn Then
            Dim centre = _toWorld.Transform(New Point3D(box.X + box.SizeX / 2, box.Y + box.SizeY / 2, box.Z))
            Dim m = obj.AbsoluteTransformation
            m.RotateAt(New Quaternion(_toWorld.Transform(New Vector3D(0, 0, 1)), 90), centre)
            obj.AbsoluteTransformation = m
            box = LocalBox(obj, True)
        End If

        Dim dx = (t.Item1 + t.Item3) / 2 - (box.X + box.SizeX / 2)
        Dim dy = (t.Item2 + t.Item4) / 2 - (box.Y + box.SizeY / 2)
        Dim dz = If(_outward > 0, -box.Z, -(box.Z + box.SizeZ))
        MoveLocal(obj, dx, dy, dz)

        Dim msg = String.Format("{0:0.#} x {1:0.#} x {2:0.#}", box.SizeX, box.SizeY, box.SizeZ) & turnedHow
        If it.Kind = ItemKind.Device AndAlso (Math.Abs(tW - box.SizeX) > 2 OrElse Math.Abs(tH - box.SizeY) > 2) Then
            msg &= String.Format(" (2D {0:0.#} x {1:0.#})", tW, tH)
        End If
        Return msg
    End Function

    ''' <summary>
    ''' Turns a rail or duct so it lies the way the 2D page shows it: tries the six ways a part can lie (90° turns
    ''' about the plane's x, y and z) and keeps the one with the smallest <see cref="FitError"/>: along the 2D line /
    ''' rectangle, a duct as wide as its rectangle, a rail wider than deep. A new MountingRail can come out standing
    ''' with its length out of the plate (only its 35 x 15 end seen from the front).
    ''' </summary>
    Private Function LayLong(obj As E3D.Placement3D, it As LayoutItem) As String
        Dim box0 = LocalBox(obj, True)
        If box0.IsEmpty Then Return ""
        Dim c = _toWorld.Transform(New Point3D(box0.X + box0.SizeX / 2, box0.Y + box0.SizeY / 2, box0.Z + box0.SizeZ / 2))
        Dim ax = _toWorld.Transform(New Vector3D(1, 0, 0)), ay = _toWorld.Transform(New Vector3D(0, 1, 0)), az = _toWorld.Transform(New Vector3D(0, 0, 1))
        Dim m0 = obj.AbsoluteTransformation
        Dim turns As New List(Of Tuple(Of String, Vector3D(), Double()))() From {
            Tuple.Create("", New Vector3D() {}, New Double() {}),
            Tuple.Create("z90", {az}, {90.0}),
            Tuple.Create("x90", {ax}, {90.0}),
            Tuple.Create("x90 z90", {ax, az}, {90.0, 90.0}),
            Tuple.Create("y90", {ay}, {90.0}),
            Tuple.Create("y90 z90", {ay, az}, {90.0, 90.0})}
        Dim best As Matrix3D = m0
        Dim bestErr = Double.MaxValue
        Dim bestName = ""
        For Each tr In turns
            Dim m = m0
            For k = 0 To tr.Item2.Length - 1
                m.RotateAt(New Quaternion(tr.Item2(k), tr.Item3(k)), c)
            Next
            Try
                obj.AbsoluteTransformation = m
                Dim e = FitError(obj, it)
                If e < bestErr - 0.01 Then bestErr = e : best = m : bestName = tr.Item1
            Catch
            End Try
        Next
        obj.AbsoluteTransformation = best
        Return If(bestName.Length = 0, "", ", turned " & bestName)
    End Function


    ''' <summary>The 2D outline on the plane: (minX, minY, maxX, maxY).</summary>
    Private Function TargetRect(it As LayoutItem) As Tuple(Of Double, Double, Double, Double)
        Dim a = Map(it.Min2D), b = Map(it.Max2D)
        Return Tuple.Create(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y))
    End Function

    ''' <summary>
    ''' Which way the page's x and y run on the plane, as plane directions (one of ±x, ±y each): page x = to the right
    ''' and page y = up, seen from the front of the plane (world z up; a plane lying flat: world y up). The plane's own
    ''' x / y can run any way (26-0266: x right, y up; a mounting panel built from a Rittal macro: x up).
    ''' </summary>
    Private Sub OrientPage()
        Dim n = _toWorld.Transform(New Vector3D(0, 0, _outward))
        n.Normalize()
        Dim up As New Vector3D(0, 0, 1)
        If Math.Abs(Vector3D.DotProduct(n, up)) > 0.9 Then up = New Vector3D(0, 1, 0)
        Dim right = Vector3D.CrossProduct(up, n)
        Dim upOnPlane = Vector3D.CrossProduct(n, right)
        Dim rl = _toLocal.Transform(right), ul = _toLocal.Transform(upOnPlane)
        If Math.Abs(rl.X) >= Math.Abs(rl.Y) Then
            _rx = Math.Sign(rl.X) : _ry = 0
            _ux = 0 : _uy = If(ul.Y >= 0, 1, -1)
        Else
            _rx = 0 : _ry = Math.Sign(rl.Y)
            _ux = If(ul.X >= 0, 1, -1) : _uy = 0
        End If
        If _rx = 0 AndAlso _ry = 0 Then _rx = 1
        If _o.FlipX Then _rx = -_rx : _ry = -_ry
        If _o.FlipY Then _ux = -_ux : _uy = -_uy
        _log(String.Format("Page x -> plane {0}, page y -> plane {1}, out of the surface = plane {2}z", AxisName(_rx, _ry), AxisName(_ux, _uy), If(_outward > 0, "+", "-")))
    End Sub

    Private Shared Function AxisName(x As Double, y As Double) As String
        If x > 0 Then Return "+x"
        If x < 0 Then Return "-x"
        If y > 0 Then Return "+y"
        Return "-y"
    End Function

    ''' <summary>Size of the surface along page x / page y.</summary>
    Private ReadOnly Property SurfWidth As Double
        Get
            Return If(_rx <> 0, _surfMaxX - _surfMinX, _surfMaxY - _surfMinY)
        End Get
    End Property

    Private ReadOnly Property SurfHeight As Double
        Get
            Return If(_ux <> 0, _surfMaxX - _surfMinX, _surfMaxY - _surfMinY)
        End Get
    End Property

    ''' <summary>
    ''' A 2D page point on the plane: from the surface's corner that is lower left seen from the front, page x along
    ''' (_rx, _ry), page y along (_ux, _uy).
    ''' </summary>
    Private Function Map(p As PointD) As P2
        Dim u = (p.X - _origin2D.X) * _scale + _o.ShiftX
        Dim v = (p.Y - _origin2D.Y) * _scale + _o.ShiftY
        Dim cx = If(_rx < 0 OrElse _ux < 0, _surfMaxX, _surfMinX)
        Dim cy = If(_ry < 0 OrElse _uy < 0, _surfMaxY, _surfMinY)
        Return New P2(cx + u * _rx + v * _ux, cy + u * _ry + v * _uy)
    End Function

    Private Sub MoveLocal(obj As E3D.Placement3D, dx As Double, dy As Double, dz As Double)
        If Math.Abs(dx) < 0.000001 AndAlso Math.Abs(dy) < 0.000001 AndAlso Math.Abs(dz) < 0.000001 Then Return
        Dim v = _toWorld.Transform(New Vector3D(dx, dy, dz))
        Dim m = obj.AbsoluteTransformation
        m.Translate(v)
        obj.AbsoluteTransformation = m
    End Sub

    ''' <summary>
    ''' A device whose 2D outline lies over a rail line goes onto that rail: its mounting point (base mate, "G1") on
    ''' the rail's line ("R1"), across the rail and in depth; along the rail it stays where it is in 2D (26-0266:
    ''' devices G1 -> R1, their middle on the middle of the rail). Without mates: its middle on the rail's middle, its
    ''' back on the rail's front. The device becomes a child of the rail.
    ''' </summary>
    Private Function SnapToRail(dev As E3D.Placement3D, it As LayoutItem, items As List(Of LayoutItem)) As String
        Dim rail As LayoutItem = Nothing
        Dim best = Double.MaxValue
        For Each r In items
            If r.Kind <> ItemKind.Rail OrElse r.Placed Is Nothing Then Continue For
            Dim d As Double
            If r.Horizontal Then
                If it.CentreX < r.Min2D.X OrElse it.CentreX > r.Max2D.X Then Continue For
                If r.CentreY < it.Min2D.Y OrElse r.CentreY > it.Max2D.Y Then Continue For
                d = Math.Abs(r.CentreY - it.CentreY)
            Else
                If it.CentreY < r.Min2D.Y OrElse it.CentreY > r.Max2D.Y Then Continue For
                If r.CentreX < it.Min2D.X OrElse r.CentreX > it.Max2D.X Then Continue For
                d = Math.Abs(r.CentreX - it.CentreX)
            End If
            If d < best Then best = d : rail = r
        Next
        If rail Is Nothing Then Return ""

        Dim railObj = rail.Placed
        Dim how = ""
        Dim src = SourcePoint(dev)
        Dim tgt = TargetPoint(railObj)
        Dim dx = 0.0, dy = 0.0, dz = 0.0
        Dim cx = If(rail.Horizontal, _ux, _rx), cy = If(rail.Horizontal, _uy, _ry)
        Dim ok = False
        If src.HasValue AndAlso tgt.HasValue Then
            Dim ps = _toLocal.Transform(src.Value), pt = _toLocal.Transform(tgt.Value)
            ' across the rail = page y for a horizontal rail line, page x for a vertical one
            Dim across = (pt.X - ps.X) * cx + (pt.Y - ps.Y) * cy
            dx = across * cx : dy = across * cy
            dz = pt.Z - ps.Z
            ok = Math.Abs(dx) <= 60 AndAlso Math.Abs(dy) <= 60 AndAlso Math.Abs(dz) <= 80
            how = "mounting point"
        End If
        If Not ok Then
            Dim db = LocalBox(dev, True), rb = LocalBox(railObj, False)
            Dim across = ((rb.X + rb.SizeX / 2) - (db.X + db.SizeX / 2)) * cx + ((rb.Y + rb.SizeY / 2) - (db.Y + db.SizeY / 2)) * cy
            dx = across * cx : dy = across * cy
            dz = If(_outward > 0, (rb.Z + rb.SizeZ) - db.Z, rb.Z - (db.Z + db.SizeZ))
            how = "middle of the rail"
        End If
        MoveLocal(dev, dx, dy, dz)
        Try
            dev.SetParent(railObj, True)
        Catch
        End Try
        Return ", on rail " & rail.PartNr & String.Format(" ({0}, moved {1:0.#} mm across)", how, dx * cx + dy * cy)
    End Function

    ' ------------------------------------------------------------------ snapping with EPLAN's own mates

    ''' <summary>The rail's line ("R1", else its first line mate).</summary>
    Private Shared Function RailLine(rail As E3D.Placement3D) As E3D.LineMate
        Try
            Dim arr = rail.GetTargetMates(False)
            If arr Is Nothing Then Return Nothing
            Dim lines = arr.Cast(Of E3D.Mate)().OfType(Of E3D.LineMate)().ToList()
            Dim r1 = lines.FirstOrDefault(Function(x) String.Equals(x.Name, "R1", StringComparison.OrdinalIgnoreCase))
            Return If(r1, lines.FirstOrDefault())
        Catch
            Return Nothing
        End Try
    End Function

    ''' <summary>
    ''' The part lies in front of the surface: its back not behind the surface (3 mm play) and its front further out.
    ''' Only checked on a mounting panel; on a door or wall a push button's body rightly goes through.
    ''' </summary>
    Private Function InFront(obj As E3D.Placement3D) As Boolean
        If Not _strictFront Then Return True
        Dim b = LocalBox(obj, True)
        If b.IsEmpty Then Return False
        Dim back = If(_outward > 0, b.Z, -(b.Z + b.SizeZ))
        Dim front = If(_outward > 0, b.Z + b.SizeZ, -b.Z)
        Return back >= -3 AndAlso front > back
    End Function

    ''' <summary>
    ''' After EPLAN's snapping: the middle of the part onto the middle of its 2D outline (on 26-0266 the 2D outline of
    ''' a part is its 3D size seen from the front, and a device on a rail has its middle on the rail's line). A part
    ''' without its own mounting point ("G1", from its 3D macro) was snapped with a point EPLAN makes on its box
    ''' (a corner, the middle "C" ...), which says nothing about its depth: its back goes against the rail's front, or
    ''' against the surface.
    ''' </summary>
    Private Function SettleOnTarget(obj As E3D.Placement3D, it As LayoutItem, rail As LayoutItem, mateName As String) As String
        Dim box = LocalBox(obj, True)
        If box.IsEmpty Then Return ""
        Dim t = TargetRect(it)
        Dim dx = (t.Item1 + t.Item3) / 2 - (box.X + box.SizeX / 2)
        Dim dy = (t.Item2 + t.Item4) / 2 - (box.Y + box.SizeY / 2)
        Dim dz = 0.0
        Dim note = ""
        If Not String.Equals(mateName, "G1", StringComparison.OrdinalIgnoreCase) Then
            Dim baseZ = 0.0
            If rail IsNot Nothing AndAlso rail.Placed IsNot Nothing Then
                Dim rb = LocalBox(rail.Placed, False)
                If Not rb.IsEmpty Then baseZ = If(_outward > 0, rb.Z + rb.SizeZ, rb.Z)
            End If
            dz = If(_outward > 0, baseZ - box.Z, baseZ - (box.Z + box.SizeZ))
            If Math.Abs(dz) > 0.5 Then note = String.Format(", back on the {0} ({1:0.#} mm)", If(rail IsNot Nothing, "rail", "surface"), dz)
        End If
        If Math.Abs(dx) > 0.05 OrElse Math.Abs(dy) > 0.05 OrElse Math.Abs(dz) > 0.05 Then MoveLocal(obj, dx, dy, dz)
        Return note
    End Function

    ''' <summary>Turns the part 90° on the plane, around its own middle.</summary>
    Private Sub TurnOnPlane(obj As E3D.Placement3D)
        Dim box = LocalBox(obj, True)
        If box.IsEmpty Then Return
        Dim centre = _toWorld.Transform(New Point3D(box.X + box.SizeX / 2, box.Y + box.SizeY / 2, box.Z))
        Dim m = obj.AbsoluteTransformation
        m.RotateAt(New Quaternion(_toWorld.Transform(New Vector3D(0, 0, 1)), 90), centre)
        obj.AbsoluteTransformation = m
    End Sub

    ''' <summary>A name without anything but prefix signs ("", "+", "=+").</summary>
    Private Shared Function IsEmptyName(n As String) As Boolean
        Return n Is Nothing OrElse n.Trim("="c, "+"c, "-"c, "#"c, "."c, " "c).Length = 0
    End Function

    ''' <summary>
    ''' Gives the parts of the cabinet that have no name yet (mounting panel, doors, walls ...: "Plaque de montage: +")
    ''' the cabinet's device tag, as EPLAN does when a cabinet is placed by hand (26-0266: every part of an enclosure
    ''' carries the enclosure's identifier). Returns how many were named.
    ''' </summary>
    Private Function NameCabinetParts(cab As E3D.Cabinet) As Integer
        If cab Is Nothing Then Return 0
        Dim nm = ""
        Try
            nm = cab.Name
        Catch
        End Try
        If IsEmptyName(nm) Then Return 0
        Dim n = 0
        Dim stack As New Stack(Of E3D.Placement3D)()
        stack.Push(cab)
        Dim guard = 0
        While stack.Count > 0 AndAlso guard < 100000
            guard += 1
            Dim p = stack.Pop()
            Dim kids As E3D.Placement3D() = Nothing
            Try
                kids = p.Children
            Catch
            End Try
            If kids Is Nothing Then Continue While
            For Each k In kids
                stack.Push(k)
                Dim f3 = TryCast(k, E3D.Function3D)
                If f3 Is Nothing Then Continue For
                Try
                    If IsEmptyName(f3.Name) Then
                        f3.Name = nm
                        n += 1
                    End If
                Catch
                End Try
            Next
        End While
        Return n
    End Function

    ''' <summary>
    ''' The part's mounting points, best first: "G1" (the macro's own), the base mate, "M2" / "M4" (rails and ducts in
    ''' 26-0266: "M2 -> MP", "M4 -> MP"), the middle of face 1 ("F1_#C", the mounting face: -OP41.01 F1_#C -> MP), the
    ''' other face middles, then all the rest (corners V.., edge middles M..). Which one gives the right result is
    ''' decided by <see cref="FitError"/>.
    ''' </summary>
    Private Shared Function SourceMates(obj As E3D.Placement3D, it As LayoutItem) As List(Of E3D.PointMate)
        Dim result As New List(Of E3D.PointMate)()
        Try
            Dim arr = obj.GetSourceMates(E3D.Mate.Enums.PlacementOptions.None)
            If arr Is Nothing OrElse arr.Length = 0 Then Return result
            Dim add = Sub(m As E3D.PointMate)
                          If m IsNot Nothing AndAlso Not result.Contains(m) Then result.Add(m)
                      End Sub
            Dim named = Function(n As String) arr.FirstOrDefault(Function(x) String.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase))
            add(named("G1"))
            For Each m In arr.Where(Function(x) x.IsBaseMate)
                add(m)
            Next
            If it.Kind = ItemKind.Rail OrElse it.Kind = ItemKind.Duct Then
                add(named("M2"))
                add(named("M4"))
            End If
            add(named("F1_#C"))
            For Each m In arr.Where(Function(x) x.Name IsNot Nothing AndAlso (x.Name.EndsWith("#C", StringComparison.OrdinalIgnoreCase) OrElse x.Name = "C"))
                add(m)
            Next
            For Each m In arr
                add(m)
            Next
        Catch
        End Try
        Return result
    End Function

    ''' <summary>
    ''' How far the part's size seen from the front is from what the 2D page says (0 = right): a device, duct or plate
    ''' part = its 2D outline (width x height); a rail = along its 2D line and wider than deep (a DIN rail 35 wide,
    ''' 15 deep). A part lying the wrong way (the 1000 mm ducts across the plate, a rail on its side, a circuit breaker
    ''' with its front up) gives a large value.
    ''' </summary>
    Private Function FitError(obj As E3D.Placement3D, it As LayoutItem) As Double
        Dim b = LocalBox(obj, True)
        If b.IsEmpty Then Return Double.MaxValue
        Dim t = TargetRect(it)
        Dim tW = t.Item3 - t.Item1, tH = t.Item4 - t.Item2
        If it.Kind = ItemKind.Rail Then
            Dim alongX = tW >= tH
            Dim along = If(alongX, b.SizeX, b.SizeY), across = If(alongX, b.SizeY, b.SizeX)
            Dim e = Math.Abs(along - Math.Max(tW, tH))
            If across < b.SizeZ Then e += 1000
            Return e
        End If
        If tW <= 0 OrElse tH <= 0 Then Return 0
        Return Math.Abs(b.SizeX - tW) + Math.Abs(b.SizeY - tH)
    End Function

    ''' <summary>Small enough to take it: 4 mm, or 8 % of the outline for large parts.</summary>
    Private Function FitTolerance(it As LayoutItem) As Double
        Dim t = TargetRect(it)
        Return Math.Max(4.0, 0.08 * ((t.Item3 - t.Item1) + (t.Item4 - t.Item2)))
    End Function

    ''' <summary>
    ''' May this mounting point snap onto the target point? Every point carries a list of the points it fits
    ''' (Mate.MatchingMateNames, '#' separated, '*' wildcards; "*-" in front = all except the ones listed: the points
    ''' EPLAN makes on a part's box have "*-#R*#L1MP#..." = not on rails). An empty list fits anything.
    ''' </summary>
    Private Shared Function MayMate(src As E3D.Mate, targetName As String) As Boolean
        Dim list = ""
        Try
            list = If(src.MatchingMateNames, "")
        Catch
        End Try
        If list.Trim().Length = 0 OrElse String.IsNullOrEmpty(targetName) Then Return True
        Dim tokens = list.Split("#"c).Select(Function(x) x.Trim()).Where(Function(x) x.Length > 0).ToList()
        Dim exclude = tokens.Count > 0 AndAlso (tokens(0) = "*-" OrElse tokens(0) = "-")
        If exclude Then tokens.RemoveAt(0)
        Dim hit = tokens.Any(Function(p) Like_(targetName, p))
        Return If(exclude, Not hit, hit)
    End Function

    Private Shared Function Like_(name As String, pattern As String) As Boolean
        Dim rx = "^" & System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\*", ".*").Replace("\?", ".") & "$"
        Return System.Text.RegularExpressions.Regex.IsMatch(name, rx, System.Text.RegularExpressions.RegexOptions.IgnoreCase)
    End Function

    ''' <summary>The points that may snap onto the target first (as EPLAN chooses when placing by hand), then the rest.</summary>
    Private Shared Function ByMatch(cands As List(Of E3D.PointMate), targetName As String) As List(Of E3D.PointMate)
        Return cands.Where(Function(m) MayMate(m, targetName)).Concat(cands.Where(Function(m) Not MayMate(m, targetName))).ToList()
    End Function

    ''' <summary>
    ''' Lets EPLAN snap the part with one of its mounting points onto the plane (PointMate.SnapTo(PlaneMate, angle,
    ''' x, y)), as when it is placed by hand ("G1" / "M2" -> "MP"): EPLAN sets which way it faces and how deep it sits,
    ''' and makes it a child of the surface. Mounting points and angles (radians as documented, and degrees) are tried
    ''' until the part lies in front of the surface the way the 2D page shows it; else the closest is used and turned
    ''' on the surface by hand when it lies across. Then slid so its middle is on the middle of the 2D outline.
    ''' </summary>
    Private Function SnapOnPlane(obj As E3D.Placement3D, it As LayoutItem, ByRef how As String, ByRef mateName As String) As Boolean
        Dim pm As E3D.PlaneMate = Nothing
        Try
            pm = _plane.BaseMate
        Catch
        End Try
        If pm Is Nothing Then Return False
        Dim pmName = ""
        Try
            pmName = pm.Name
        Catch
        End Try
        Dim cands = ByMatch(SourceMates(obj, it), If(String.IsNullOrEmpty(pmName), "MP", pmName)).Take(30).ToList()
        If cands.Count = 0 Then Return False
        ' a device with its own point for the surface ("G1" -> "MP"): as EPLAN snaps it by hand, then slid onto
        ' its 2D position; not turned, depth as EPLAN sets it
        If it.Kind = ItemKind.Device Then
            Dim own = OwnPoint(SourceMates(obj, it), If(String.IsNullOrEmpty(pmName), "MP", pmName))
            If own IsNot Nothing Then
                Try
                    own.SnapTo(pm, 0.0, 0.0, 0.0)
                    ' where the mounting point is in the part: the snap just put it on the plane mate's origin
                    Dim g1Local = SnappedPoint(obj, pm)
                    Dim t0 = TargetRect(it)
                    Dim b0 = LocalBox(obj, True)
                    Dim dx0 = (t0.Item1 + t0.Item3) / 2 - (b0.X + b0.SizeX / 2)
                    Dim dy0 = (t0.Item2 + t0.Item4) / 2 - (b0.Y + b0.SizeY / 2)
                    own.SnapTo(pm, 0.0, dx0, dy0)
                    Dim b1 = LocalBox(obj, True)
                    Dim ex1 = (t0.Item1 + t0.Item3) / 2 - (b1.X + b1.SizeX / 2)
                    Dim ey1 = (t0.Item2 + t0.Item4) / 2 - (b1.Y + b1.SizeY / 2)
                    If Math.Abs(ex1) > 1 OrElse Math.Abs(ey1) > 1 Then MoveLocal(obj, ex1, ey1, 0)
                    Dim alignNote = ""
                    If AlignOnPlane(obj, it, own, alignNote, g1Local) Then
                        _aligned = True
                        how = ", snapped onto the surface (" & own.Name & ", own mounting point, aligned with the surface" & alignNote & ")"
                    Else
                        how = ", snapped onto the surface (" & own.Name & ", own mounting point" & alignNote & TipBack(obj, it, own) & ")"
                    End If
                    mateName = "#own"
                    Return True
                Catch ex As Exception
                    how = ", own mounting point " & own.Name & " failed: " & ex.Message
                End Try
            End If
        End If
        Dim tol = FitTolerance(it)
        Dim best As E3D.PointMate = Nothing
        Dim bestAngle = 0.0
        Dim bestErr = Double.MaxValue
        Dim lastError = ""
        Dim found = False
        For Each src In cands
            For Each ang In {0.0, Math.PI / 2, 90.0}
                Try
                    src.SnapTo(pm, ang, 0.0, 0.0)
                    If Not InFront(obj) Then Continue For
                    Dim e = FitError(obj, it)
                    If e < bestErr Then bestErr = e : best = src : bestAngle = ang
                    If e <= tol Then found = True : Exit For
                Catch ex As Exception
                    lastError = src.Name & ": " & ex.Message
                End Try
            Next
            If found Then Exit For
        Next
        If best Is Nothing Then
            how = ", snapping onto the surface failed" & If(lastError.Length > 0, " (" & lastError & ")", "")
            Return False
        End If
        Try
            src_Snap(best, pm, bestAngle)
            Dim turned = ""
            If Not found Then
                ' none lies right: turned on the surface by hand when that is closer
                Dim before = FitError(obj, it)
                TurnOnPlane(obj)
                If FitError(obj, it) < before Then
                    turned = ", turned"
                Else
                    src_Snap(best, pm, bestAngle)
                End If
            End If
            Dim t = TargetRect(it)
            Dim box = LocalBox(obj, True)
            MoveLocal(obj, (t.Item1 + t.Item3) / 2 - (box.X + box.SizeX / 2), (t.Item2 + t.Item4) / 2 - (box.Y + box.SizeY / 2), 0)
            how = ", snapped onto the surface (" & best.Name & turned & If(found, "", String.Format(", closest fit {0:0} mm off", FitError(obj, it))) & ")"
            mateName = best.Name
            Return True
        Catch ex As Exception
            how = ", snapping onto the surface failed: " & ex.Message
            Return False
        End Try
    End Function

    Private Shared Sub src_Snap(src As E3D.PointMate, pm As E3D.PlaneMate, angle As Double)
        src.SnapTo(pm, angle, 0.0, 0.0)
    End Sub

    ''' <summary>
    ''' A device that carries others on a mounting line of its own (the S7-1500 mounting rail SIE.6ES7590-1AE80-0AA0
    ''' under the CPU and I/O modules): an already placed device whose 2D outline holds this device's middle, is
    ''' larger, and has a line mate the device's own mounting point may snap onto. Returns the carrier and that line.
    ''' </summary>
    Private Function CarrierUnder(dev As E3D.Placement3D, it As LayoutItem, items As List(Of LayoutItem), ByRef line As E3D.LineMate, ByRef own As E3D.PointMate) As LayoutItem
        Dim area = it.Width2D * it.Height2D
        Dim srcs = SourceMates(dev, it)
        For Each c In items.Where(Function(x) x IsNot it AndAlso x.Kind = ItemKind.Device AndAlso x.Placed IsNot Nothing AndAlso
                                               x.Width2D * x.Height2D > area * 1.2 AndAlso x.Contains(it.CentreX, it.CentreY)).
                            OrderBy(Function(x) x.Width2D * x.Height2D)
            Dim lines As List(Of E3D.LineMate) = Nothing
            Try
                Dim arr = c.Placed.GetTargetMates(False)
                If arr IsNot Nothing Then lines = arr.Cast(Of E3D.Mate)().OfType(Of E3D.LineMate)().ToList()
            Catch
            End Try
            If lines Is Nothing OrElse lines.Count = 0 Then Continue For
            For Each lm In lines
                Dim o = OwnPoint(srcs, lm.Name)
                If o IsNot Nothing AndAlso MayMate(o, lm.Name) Then
                    line = lm
                    own = o
                    Return c
                End If
            Next
        Next
        Return Nothing
    End Function

    ''' <summary>
    ''' Snaps the device with its own point onto the carrier's line (as onto a DIN rail: by hand EPLAN puts the S7-1500
    ''' modules onto the line of their mounting rail), slid along it to the 2D position, tipped back when EPLAN's
    ''' snapping left it lying.
    ''' </summary>
    Private Function SnapOnCarrier(dev As E3D.Placement3D, it As LayoutItem, carrier As LayoutItem, lm As E3D.LineMate, own As E3D.PointMate, ByRef how As String) As Boolean
        Try
            ' slid along the carrier's long side on the page
            SnapOwnOnRail(dev, it, carrier, own, lm)
            Dim fix = If(AlignOnCarrier(dev, it, carrier, own, lm), ", aligned with the plate", TipBack(dev, it, own))
            how = ", snapped onto " & If(String.IsNullOrEmpty(carrier.Name), carrier.PartNr, carrier.Name) & " " & carrier.PartNr &
                  " (" & own.Name & " -> " & lm.Name & ", own mounting point" & fix & ")"
            Return True
        Catch ex As Exception
            how = ", snapping onto " & carrier.PartNr & " failed: " & ex.Message
            Return False
        End Try
    End Function

    ''' <summary>
    ''' After the snap onto the carrier's line EPLAN leaves an S7-1500 module tipped 90° (lying on its side on the
    ''' mounting rail). Placed by hand the module's mounting point (at the bottom of its back, where it rests on the
    ''' rail's lower lip) has z out of the plate, y up and x along the rail: the same frame as the plate seen from the
    ''' front. So the device is put so its mounting point has that frame, at the point on the line where the snap put
    ''' it, then slid along the line to its 2D position. Only for a carrier lying across the page (a vertical one keeps
    ''' EPLAN's snap and the tip back).
    ''' </summary>
    Private Function AlignOnCarrier(dev As E3D.Placement3D, it As LayoutItem, carrier As LayoutItem, own As E3D.PointMate, lm As E3D.LineMate) As Boolean
        If Not carrier.Horizontal Then Return False
        Try
            ' Depth: in 26-0266 the modules' mounting point G1 sits on the front of the S7-1500 mounting rail
            ' (rail z 0..15.7 on the plate, G1 at 15.7; the module's back reaches a bit behind it, to 5.3 / 6).
            ' The point EPLAN's snap reports did not give that (the modules floated in front of the rail), so
            ' the rail's front is used. Height and position along the rail: the 2D outline.
            Dim cb = LocalBox(carrier.Placed, False)
            If cb.IsEmpty Then cb = LocalBox(carrier.Placed, True)
            If cb.IsEmpty Then Return False
            Dim front = If(_outward > 0, cb.Z + cb.SizeZ, cb.Z)
            Dim t = TargetRect(it)
            Dim target = _toWorld.Transform(New Point3D((t.Item1 + t.Item3) / 2, (t.Item2 + t.Item4) / 2, front))
            Dim tRel = MateFrame(dev, own)
            If tRel.HasValue Then
                Dim inv = tRel.Value
                inv.Invert()
                dev.AbsoluteTransformation = Matrix3D.Multiply(inv, MountFrame(target))
            Else
                ' no usable mate: the part's own axes get the plate's frame (as on the door), back on the rail
                Dim zL As New Vector3D(0, 0, _outward)
                Dim yL As New Vector3D(_ux, _uy, 0)
                Dim xL = Vector3D.CrossProduct(yL, zL)
                Dim xW = _toWorld.Transform(xL), yW = _toWorld.Transform(yL), zW = _toWorld.Transform(zL)
                xW.Normalize() : yW.Normalize() : zW.Normalize()
                dev.AbsoluteTransformation = New Matrix3D(xW.X, xW.Y, xW.Z, 0, yW.X, yW.Y, yW.Z, 0, zW.X, zW.Y, zW.Z, 0, target.X, target.Y, target.Z, 1)
                BackOn(dev, front)
            End If
            ' its middle onto the middle of the 2D outline; depth stays
            Dim b = LocalBox(dev, True)
            MoveLocal(dev, (t.Item1 + t.Item3) / 2 - (b.X + b.SizeX / 2), (t.Item2 + t.Item4) / 2 - (b.Y + b.SizeY / 2), 0)
            Return True
        Catch
            Return False
        End Try
    End Function

    ''' <summary>Moves the part out of / into the surface so its back lies at depth z (plane coordinates).</summary>
    Private Function BackOn(dev As E3D.Placement3D, z As Double) As Double
        Dim b = LocalBox(dev, True)
        If b.IsEmpty Then Return 0
        Dim back = If(_outward > 0, b.Z, b.Z + b.SizeZ)
        Dim dz = z - back
        If Math.Abs(dz) > 0.05 Then MoveLocal(dev, 0, 0, dz)
        Return dz
    End Function

    ''' <summary>
    ''' On a mounting panel a part not on a rail lies with its back on the panel (26-0266: the S7-1500 mounting rail
    ''' z 0..15.7). The mounting point the API reports does not always give that (the S7-1500 mounting rail sank
    ''' into the plate), so the back is put on the surface.
    ''' </summary>
    Private Function BackOnSurface(dev As E3D.Placement3D, it As LayoutItem, items As List(Of LayoutItem)) As String
        If Not _strictFront Then Return ""
        Try
            ' on a larger part placed before it (the S7-1500 modules on their mounting rail, which has no line to
            ' snap onto: in 26-0266 they are snapped onto the panel, their back on the rail's front at 15.7 mm)
            Dim under = items.Where(Function(x) x IsNot it AndAlso x.Kind = ItemKind.Device AndAlso x.Placed IsNot Nothing AndAlso
                                                x.Width2D * x.Height2D > it.Width2D * it.Height2D * 1.2 AndAlso
                                                x.Contains(it.CentreX, it.CentreY)).
                              OrderBy(Function(x) x.Width2D * x.Height2D).FirstOrDefault()
            Dim z = 0.0
            Dim onWhat = "the surface"
            If under IsNot Nothing Then
                Dim cb = LocalBox(under.Placed, False)
                If cb.IsEmpty Then cb = LocalBox(under.Placed, True)
                If Not cb.IsEmpty Then
                    z = If(_outward > 0, cb.Z + cb.SizeZ, cb.Z)
                    onWhat = If(String.IsNullOrEmpty(under.Name), under.PartNr, under.Name & " " & under.PartNr)
                End If
            End If
            Dim dz = BackOn(dev, z)
            If Math.Abs(dz) <= 0.5 Then Return ""
            Return String.Format(", back put on {0} ({1:0.#} mm {2})", onWhat, Math.Abs(dz), If(dz * _outward > 0, "out", "in"))
        Catch
            Return ""
        End Try
    End Function

    ''' <summary>
    ''' The frame the device's mounting point (as the API reports it) must get on the surface, at point p (world).
    ''' Placed by hand (26-0266, door +APS_201 and mounting panel) the mounting point's frame is the surface's frame:
    ''' z out of the surface, y up, x to the right. The frame the API gives through Mate.Transformation is that one
    ''' turned 180° about x (z into the surface, y down): aligned with the surface itself everything on the door
    ''' faced into the cabinet; turned about the vertical (z in, y up) it faced out but stood upside down
    ''' (+APS_201/9: -M11, the push buttons). So: z into the surface, y down the page, x = y x z (to the right).
    ''' </summary>
    Private Function MountFrame(p As Point3D) As Matrix3D
        Dim zL As New Vector3D(0, 0, -_outward)
        Dim yL As New Vector3D(-_ux, -_uy, 0)
        Dim xL = Vector3D.CrossProduct(yL, zL)
        Dim xW = _toWorld.Transform(xL), yW = _toWorld.Transform(yL), zW = _toWorld.Transform(zL)
        xW.Normalize() : yW.Normalize() : zW.Normalize()
        Return New Matrix3D(xW.X, xW.Y, xW.Z, 0, yW.X, yW.Y, yW.Z, 0, zW.X, zW.Y, zW.Z, 0, p.X, p.Y, p.Z, 1)
    End Function

    ''' <summary>
    ''' The frame of a device's mate relative to the device. Mate.Transformation is documented both as relative to
    ''' the device and as absolute: the reading whose origin lies on the device is used.
    ''' </summary>
    Private Shared Function MateFrame(dev As E3D.Placement3D, own As E3D.Mate) As Matrix3D?
        Dim mA = dev.AbsoluteTransformation
        Dim mT = own.Transformation
        Dim box = dev.GetBoundingBox(True)
        If box.IsEmpty Then Return Nothing
        Dim onPart = Function(q As Point3D) As Boolean
                         Return q.X >= box.X - 25 AndAlso q.X <= box.X + box.SizeX + 25 AndAlso
                                q.Y >= box.Y - 25 AndAlso q.Y <= box.Y + box.SizeY + 25 AndAlso
                                q.Z >= box.Z - 25 AndAlso q.Z <= box.Z + box.SizeZ + 25
                     End Function
        Dim origin As New Point3D(0, 0, 0)
        Dim tRel As Matrix3D
        If onPart(Matrix3D.Multiply(mT, mA).Transform(origin)) Then
            tRel = mT
        ElseIf onPart(mT.Transform(origin)) Then
            Dim aInv = mA
            aInv.Invert()
            tRel = Matrix3D.Multiply(mT, aInv)
        Else
            Return Nothing
        End If
        If Not tRel.HasInverse Then Return Nothing
        Return tRel
    End Function

    ''' <summary>
    ''' The device's own point for this target, as EPLAN uses it when placing by hand: "G1", or a point whose
    ''' MatchingMateNames explicitly lists the target ("R1" on a rail, "MP" on a surface). Nothing for a part
    ''' without a 3D macro (its box points exclude rails: "*-#R*...").
    ''' </summary>
    Private Shared Function OwnPoint(cands As List(Of E3D.PointMate), targetName As String) As E3D.PointMate
        Dim g1 = cands.FirstOrDefault(Function(m) String.Equals(m.Name, "G1", StringComparison.OrdinalIgnoreCase))
        For Each m In cands
            Dim list = ""
            Try
                list = If(m.MatchingMateNames, "")
            Catch
            End Try
            If list.Trim().Length = 0 Then Continue For
            If list.TrimStart().StartsWith("*-") OrElse list.TrimStart().StartsWith("-") Then Continue For
            If MayMate(m, targetName) Then Return m
        Next
        Return g1
    End Function

    ''' <summary>
    ''' The turn set in the grid's "Turn" column (remembered per part number): "90" / "180" / "270" = turned that
    ''' far on the plate (about the plate's normal), "flip" = front and back swapped (180 degrees about the page's
    ''' vertical). Both about the part's own middle, so it stays where it was placed; for 90 / 270 its middle goes
    ''' back onto the 2D outline.
    ''' </summary>
    Private Function ApplyTurn(obj As E3D.Placement3D, it As LayoutItem) As String
        Dim turn = If(it.Turn, "").Trim().ToLowerInvariant()
        If turn.Length = 0 OrElse turn = "0" Then Return ""
        Dim b = LocalBox(obj, True)
        If b.IsEmpty Then Return ""
        Dim centre = _toWorld.Transform(New Point3D(b.X + b.SizeX / 2, b.Y + b.SizeY / 2, b.Z + b.SizeZ / 2))
        Dim axis As Vector3D
        Dim deg As Double
        If turn = "flip" Then
            axis = New Vector3D(_ux, _uy, 0)
            deg = 180
        Else
            If Not Double.TryParse(turn, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, deg) Then Return ""
            axis = New Vector3D(0, 0, 1)
        End If
        Dim m = obj.AbsoluteTransformation
        m.RotateAt(New Quaternion(_toWorld.Transform(axis), deg), centre)
        obj.AbsoluteTransformation = m
        Dim t = TargetRect(it)
        Dim b2 = LocalBox(obj, True)
        MoveLocal(obj, (t.Item1 + t.Item3) / 2 - (b2.X + b2.SizeX / 2), (t.Item2 + t.Item4) / 2 - (b2.Y + b2.SizeY / 2), 0)
        Return ", turned " & turn & If(turn = "flip", "", "°") & " (Turn column)"
    End Function

    ''' <summary>
    ''' A device through a door or side wall (push button, HMI, fan) has its front on the outside and its body
    ''' behind the surface. When more of it sticks out in front of the surface than lies behind it, it is the wrong
    ''' way round (the HMI's mounting point frame runs the other way than a push button's, so it came out with its
    ''' body outside and its screen facing into the cabinet): turned 180° about the page's vertical through the
    ''' surface, so the part in front and the part behind swap. Not on a mounting panel, and not for a part that lies
    ''' wholly in front of the surface (surface mounted, e.g. a document pocket on the inside of the door).
    ''' </summary>
    Private Function FaceOut(dev As E3D.Placement3D, it As LayoutItem) As String
        If _strictFront Then Return ""
        Try
            Dim b = LocalBox(dev, True)
            If b.IsEmpty Then Return ""
            Dim front = If(_outward > 0, b.Z + b.SizeZ, -b.Z)
            Dim back = If(_outward > 0, -b.Z, b.Z + b.SizeZ)
            If back < 1 OrElse front <= back + 2 Then Return ""
            Dim pivot = _toWorld.Transform(New Point3D(b.X + b.SizeX / 2, b.Y + b.SizeY / 2, 0))
            Dim m = dev.AbsoluteTransformation
            m.RotateAt(New Quaternion(_toWorld.Transform(New Vector3D(_ux, _uy, 0)), 180), pivot)
            dev.AbsoluteTransformation = m
            Dim t = TargetRect(it)
            Dim b2 = LocalBox(dev, True)
            MoveLocal(dev, (t.Item1 + t.Item3) / 2 - (b2.X + b2.SizeX / 2), (t.Item2 + t.Item4) / 2 - (b2.Y + b2.SizeY / 2), 0)
            Return String.Format(", turned round: {0:0} mm stuck out and {1:0} mm was behind the surface", front, back)
        Catch
            Return ""
        End Try
    End Function

    ''' <summary>
    ''' Sets the device's orientation on a surface the way EPLAN has it after placing by hand: in 26-0266, 68 of the 72
    ''' devices snapped "G1 -> MP" have the frame of their mounting point equal to the surface's frame (z = out of the
    ''' surface, x along the surface's x). EPLAN's snapping through the API leaves them the other way round (the HMI,
    ''' fan and emergency stop on the door facing into the cabinet; on the mounting panel the PLC tipped 90°). The
    ''' mounting point's frame relative to the device is read from Mate.Transformation (documented both as relative
    ''' to the device and as absolute: the reading whose origin lies on the device is used); the device is then put
    ''' so that frame equals the plane's frame at the middle of the 2D outline, and slid so its middle is there.
    ''' </summary>
    Private Function AlignOnPlane(dev As E3D.Placement3D, it As LayoutItem, own As E3D.PointMate, ByRef note As String, Optional g1Local As Point3D? = Nothing) As Boolean
        note = ""
        Try
            Dim t = TargetRect(it)
            Dim target = _toWorld.Transform(New Point3D((t.Item1 + t.Item3) / 2, (t.Item2 + t.Item4) / 2, 0))
            Dim tRel = MateFrame(dev, own)
            If tRel.HasValue Then
                ' the mounting point's frame as the API reports it onto MountFrame (see there)
                Dim inv = tRel.Value
                inv.Invert()
                dev.AbsoluteTransformation = Matrix3D.Multiply(inv, MountFrame(target))
            Else
                ' No usable Mate.Transformation (the HMI 6AV2128-3UB40 on +APS_201/9 came out backwards and upside
                ' down). In the 3D macros of 26-0266 (HMI, fan, push buttons, PLC) the mounting point "G1" and the
                ' box points have the same axes as the part itself, and placed by hand they have the surface's
                ' frame: so the part itself gets the surface's frame (x right, y up, z out of the surface), with
                ' its mounting point on the surface when its position can be read, else its origin.
                Dim mA = dev.AbsoluteTransformation
                Dim aInv = mA
                If Not aInv.HasInverse Then Return False
                aInv.Invert()
                ' The depth of the mounting point in the part: EPLAN's snap just put it on the surface, so when the
                ' surface lies across the part's z axis (as it does for these macros), the part's z there is the
                ' mounting point's z. (Without it the HMI sat with its front in the door instead of its bezel on it.)
                Dim local As New Point3D(0, 0, 0)
                Dim how = ", by the part's own axes and origin"
                Dim nd = aInv.Transform(_toWorld.Transform(New Vector3D(0, 0, 1)))
                If nd.Length > 0 Then nd.Normalize()
                If g1Local.HasValue Then
                    ' the mounting point as EPLAN's first snap put it on the plane mate's origin (the HMI
                    ' 6AV2128-3KB40 on +TD_203/9 came out lying, so the depth below could not be read)
                    local = g1Local.Value
                    how = ", by the part's own axes"
                ElseIf Math.Abs(nd.Z) > 0.99 Then
                    Dim q0 = aInv.Transform(_toWorld.Transform(New Point3D(0, 0, 0)))
                    local = New Point3D(0, 0, q0.Z + (q0.X * nd.X + q0.Y * nd.Y) / nd.Z)
                    how = ", by the part's own axes"
                Else
                    Dim mp As Point3D? = Nothing
                    Try
                        mp = MatePoint(own, dev)
                    Catch
                    End Try
                    If mp.HasValue Then
                        local = aInv.Transform(mp.Value)
                        how = ", by the part's own axes"
                    End If
                End If
                Dim zL As New Vector3D(0, 0, _outward)
                Dim yL As New Vector3D(_ux, _uy, 0)
                Dim xL = Vector3D.CrossProduct(yL, zL)
                Dim xW = _toWorld.Transform(xL), yW = _toWorld.Transform(yL), zW = _toWorld.Transform(zL)
                xW.Normalize() : yW.Normalize() : zW.Normalize()
                Dim r As New Matrix3D(xW.X, xW.Y, xW.Z, 0, yW.X, yW.Y, yW.Z, 0, zW.X, zW.Y, zW.Z, 0, 0, 0, 0, 1)
                Dim shift = r.Transform(New Vector3D(local.X, local.Y, local.Z))
                r.OffsetX = target.X - shift.X : r.OffsetY = target.Y - shift.Y : r.OffsetZ = target.Z - shift.Z
                dev.AbsoluteTransformation = r
                note = how
            End If
            ' its middle onto the middle of the 2D outline (the mounting point need not be in the middle)
            Dim b2 = LocalBox(dev, True)
            MoveLocal(dev, (t.Item1 + t.Item3) / 2 - (b2.X + b2.SizeX / 2), (t.Item2 + t.Item4) / 2 - (b2.Y + b2.SizeY / 2), 0)
            Return True
        Catch ex As Exception
            note = ", aligning failed: " & ex.Message
            Return False
        End Try
    End Function

    ''' <summary>
    ''' EPLAN's snapping through the API (PointMate.SnapTo) leaves a device tipped 90° about the rail, also with its
    ''' own mounting point: on +APS_201/8 every own-point snap had its 2D height in the depth (LC1D09 44.8 x 95.6 x 77
    ''' for 2D 45 x 77, iC60 36 x 78.5 x 85 for 36 x 85, the S7-1500 CPU 69.8 x 129.7 x 147 for 70 x 147). When the
    ''' 2D height (or width) matches the depth better than the height on the plate, the device is tipped back 90°
    ''' about the mounting point, so it swings forward away from the plate with its mounting side where EPLAN put it.
    ''' Then its middle goes onto the middle of the 2D outline (on the rail: the rail's line).
    ''' </summary>
    Private Function TipBack(dev As E3D.Placement3D, it As LayoutItem, own As E3D.PointMate) As String
        Dim b = LocalBox(dev, True)
        If b.IsEmpty Then Return ""
        Dim t = TargetRect(it)
        Dim tW = t.Item3 - t.Item1, tH = t.Item4 - t.Item2
        If tW <= 0 OrElse tH <= 0 Then Return ""
        Dim v As Vector3D
        ' 1 mm margin: the 2D outline is normally the 3D part's front view (exact), but not always: the iC60
        ' SE.A9F74304 on +APS_201 is 54 x 85 in 2D and 53.1 x 93.8 x 74 in 3D; snapped it lay with 74 up and 93.8
        ' deep (11 mm off in height against 8.8 tipped back), which a 3 mm margin left lying.
        If Math.Abs(b.SizeZ - tH) + 1 < Math.Abs(b.SizeY - tH) AndAlso Math.Abs(b.SizeX - tW) <= Math.Abs(b.SizeZ - tW) Then
            v = New Vector3D(0, 1, 0)
        ElseIf Math.Abs(b.SizeZ - tW) + 1 < Math.Abs(b.SizeX - tW) AndAlso Math.Abs(b.SizeY - tH) <= Math.Abs(b.SizeZ - tH) Then
            v = New Vector3D(1, 0, 0)
        Else
            Return ""
        End If
        ' the mounting point (pivot) and which way the device runs from it along v
        Dim centre As New Point3D(b.X + b.SizeX / 2, b.Y + b.SizeY / 2, b.Z + b.SizeZ / 2)
        Dim pivot As Point3D
        Dim mp = MatePoint(own, dev)
        If mp.HasValue Then
            pivot = _toLocal.Transform(mp.Value)
        Else
            ' no usable mate position: the side of the box nearest the plate, middle
            pivot = New Point3D(centre.X, centre.Y, If(_outward > 0, b.Z, b.Z + b.SizeZ))
        End If
        Dim s = Vector3D.DotProduct(centre - pivot, v)
        If Math.Abs(s) < 0.5 Then s = 1
        v = v * Math.Sign(s)
        ' turning v onto the outward normal n: 90 degrees about v x n
        Dim n As New Vector3D(0, 0, _outward)
        Dim w = Vector3D.CrossProduct(v, n)
        Dim m = dev.AbsoluteTransformation
        m.RotateAt(New Quaternion(_toWorld.Transform(w), 90), _toWorld.Transform(pivot))
        dev.AbsoluteTransformation = m
        ' middle back onto the 2D outline (depth as it is now)
        Dim b2 = LocalBox(dev, True)
        MoveLocal(dev, (t.Item1 + t.Item3) / 2 - (b2.X + b2.SizeX / 2), (t.Item2 + t.Item4) / 2 - (b2.Y + b2.SizeY / 2), 0)
        Return ", tipped back 90°"
    End Function

    ''' <summary>
    ''' Snaps the device with its own point onto the rail exactly as EPLAN does by hand (no other points tried, not
    ''' turned, depth left as EPLAN sets it), then slides it along the rail to its 2D position: the snap point's
    ''' position along the line (the line's x may run either way: whichever sign brings it there).
    ''' </summary>
    Private Function SnapOwnOnRail(dev As E3D.Placement3D, it As LayoutItem, rail As LayoutItem, src As E3D.PointMate, lm As E3D.LineMate) As Boolean
        Dim ax = If(rail.Horizontal, _rx, _ux), ay = If(rail.Horizontal, _ry, _uy)
        Dim t = TargetRect(it)
        Dim tx = (t.Item1 + t.Item3) / 2, ty = (t.Item2 + t.Item4) / 2
        Dim errAlong = Function() As Double
                           Dim b = LocalBox(dev, True)
                           Return (tx - (b.X + b.SizeX / 2)) * ax + (ty - (b.Y + b.SizeY / 2)) * ay
                       End Function
        src.SnapTo(lm, 0.0)
        Dim e0 = errAlong()
        If Math.Abs(e0) <= 0.5 Then Return True
        src.SnapTo(lm, e0)
        Dim e1 = errAlong()
        If Math.Abs(e1) <= 0.5 Then Return True
        src.SnapTo(lm, -e0)
        Dim e2 = errAlong()
        If Math.Abs(e2) <= Math.Abs(e1) Then Return True
        src.SnapTo(lm, e0)
        Return True
    End Function

    ''' <summary>
    ''' Lets EPLAN snap the device onto the rail's line (PointMate.SnapTo(LineMate, x), "G1" -> "R1"). Mounting points
    ''' are tried until the device lies in front of the plate the way the 2D page shows it (width x height; a circuit
    ''' breaker snapped with its front up has height and depth swapped); else the closest is used and, when that is
    ''' closer, turned 90° about the rail by hand. Then along the line to the 2D position.
    ''' </summary>
    Private Function SnapOnRail(dev As E3D.Placement3D, it As LayoutItem, rail As LayoutItem, ByRef how As String, ByRef mateName As String) As Boolean
        Dim lm = RailLine(rail.Placed)
        If lm Is Nothing Then Return False
        Dim cands = ByMatch(SourceMates(dev, it), lm.Name).Take(30).ToList()
        If cands.Count = 0 Then Return False
        ' a device with its own point for the rail: as EPLAN snaps it by hand, nothing else
        Dim own = OwnPoint(SourceMates(dev, it), lm.Name)
        If own IsNot Nothing Then
            Try
                SnapOwnOnRail(dev, it, rail, own, lm)
                how = ", snapped onto rail " & rail.PartNr & " (" & own.Name & " -> " & lm.Name & ", own mounting point" & TipBack(dev, it, own) & ")"
                mateName = "#own"
                Return True
            Catch ex As Exception
                how = ", own mounting point " & own.Name & " failed: " & ex.Message
            End Try
        End If
        Dim tol = FitTolerance(it)
        Dim best As E3D.PointMate = Nothing
        Dim bestErr = Double.MaxValue
        Dim lastError = ""
        Dim found = False
        For Each src In cands
            Try
                src.SnapTo(lm, 0.0)
                If Not InFront(dev) Then Continue For
                Dim e = FitError(dev, it)
                If e < bestErr Then bestErr = e : best = src
                If e <= tol Then found = True : Exit For
            Catch ex As Exception
                lastError = src.Name & ": " & ex.Message
            End Try
        Next
        If best Is Nothing Then
            how = ", snapping onto the rail failed" & If(lastError.Length > 0, " (" & lastError & ")", "")
            Return False
        End If
        Try
            ' The one found is the last one snapped: left as it is. Snapping the same point again starts from
            ' another position and EPLAN can leave the part lying (COM.S9-M / COM.S10 on +APS_201: 3D 22.8 x 47 x 75
            ' for 2D 22.8 x 75, its height in the depth).
            If Not found Then best.SnapTo(lm, 0.0)
            Dim turned = ""
            If Not found Then
                Dim before = FitError(dev, it)
                ' along the rail on the plane: page x for a horizontal rail line, page y for a vertical one
                Dim axisLocal = If(rail.Horizontal, New Vector3D(_rx, _ry, 0), New Vector3D(_ux, _uy, 0))
                TurnAbout(dev, axisLocal, 90)
                If FitError(dev, it) < before Then
                    turned = ", turned about the rail"
                Else
                    TurnAbout(dev, axisLocal, -90)
                End If
            End If
            ' still lying (height or width in the depth): tipped back about the mounting point
            If FitError(dev, it) > tol Then turned &= TipBack(dev, it, best)
            ' along the rail to the 2D position (SettleOnTarget then sets the exact middle)
            Dim t = TargetRect(it)
            Dim box = LocalBox(dev, True)
            MoveLocal(dev, (t.Item1 + t.Item3) / 2 - (box.X + box.SizeX / 2), (t.Item2 + t.Item4) / 2 - (box.Y + box.SizeY / 2), 0)
            how = ", snapped onto rail " & rail.PartNr & " (" & best.Name & " -> " & lm.Name & turned &
                  If(found, "", String.Format(", closest fit {0:0} mm off", FitError(dev, it))) & ")"
            mateName = best.Name
            Return True
        Catch ex As Exception
            how = ", snapping onto the rail failed: " & ex.Message
            Return False
        End Try
    End Function

    ''' <summary>Turns the part about an axis of the plane (plane coordinates) through its own middle, in degrees.</summary>
    Private Sub TurnAbout(obj As E3D.Placement3D, axisLocal As Vector3D, degrees As Double)
        Dim box = LocalBox(obj, True)
        If box.IsEmpty Then Return
        Dim centre = _toWorld.Transform(New Point3D(box.X + box.SizeX / 2, box.Y + box.SizeY / 2, box.Z + box.SizeZ / 2))
        Dim m = obj.AbsoluteTransformation
        m.RotateAt(New Quaternion(_toWorld.Transform(axisLocal), degrees), centre)
        obj.AbsoluteTransformation = m
    End Sub

    ''' <summary>Rails and ducts: turned when their long side lies the other way than in 2D; devices: when their outline fits better turned.</summary>
    Private Shared Function NeedsTurn(it As LayoutItem, t As Tuple(Of Double, Double, Double, Double), box As Rect3D) As Boolean
        Dim tW = t.Item3 - t.Item1, tH = t.Item4 - t.Item2
        If it.Kind = ItemKind.Rail OrElse it.Kind = ItemKind.Duct Then Return (tW >= tH) <> (box.SizeX >= box.SizeY)
        If tW <= 0 OrElse tH <= 0 Then Return False
        Dim keep = Math.Abs(tW - box.SizeX) + Math.Abs(tH - box.SizeY)
        Dim swap = Math.Abs(tW - box.SizeY) + Math.Abs(tH - box.SizeX)
        Return swap + 0.5 < keep
    End Function

    ''' <summary>The rail line the device's 2D outline lies over (the nearest), or Nothing.</summary>
    Private Shared Function RailUnder(it As LayoutItem, items As List(Of LayoutItem)) As LayoutItem
        Dim rail As LayoutItem = Nothing
        Dim best = Double.MaxValue
        For Each r In items
            If r.Kind <> ItemKind.Rail OrElse r.Placed Is Nothing Then Continue For
            Dim d As Double
            If r.Horizontal Then
                If it.CentreX < r.Min2D.X OrElse it.CentreX > r.Max2D.X Then Continue For
                If r.CentreY < it.Min2D.Y OrElse r.CentreY > it.Max2D.Y Then Continue For
                d = Math.Abs(r.CentreY - it.CentreY)
            Else
                If it.CentreY < r.Min2D.Y OrElse it.CentreY > r.Max2D.Y Then Continue For
                If r.CentreX < it.Min2D.X OrElse r.CentreX > it.Max2D.X Then Continue For
                d = Math.Abs(r.CentreX - it.CentreX)
            End If
            If d < best Then best = d : rail = r
        Next
        Return rail
    End Function

    Private Function SizeText(obj As E3D.Placement3D, it As LayoutItem) As String
        Dim box = LocalBox(obj, True)
        If box.IsEmpty Then Return "(no size)"
        Dim t = TargetRect(it)
        Dim tW = t.Item3 - t.Item1, tH = t.Item4 - t.Item2
        Dim msg = String.Format("{0:0.#} x {1:0.#} x {2:0.#}", box.SizeX, box.SizeY, box.SizeZ)
        If it.Kind = ItemKind.Device AndAlso (Math.Abs(tW - box.SizeX) > 2 OrElse Math.Abs(tH - box.SizeY) > 2) Then
            msg &= String.Format(" (2D {0:0.#} x {1:0.#})", tW, tH)
        End If
        Return msg
    End Function

    ''' <summary>
    ''' A device that is not on a rail (a push button in a door, a power supply screwed to the plate) goes with its
    ''' mounting point (base mate, "G1") onto the surface, as EPLAN snaps it ("G1" -> "MP"): the part of a push button
    ''' behind its mounting face goes through the door. Without a mounting point its back stays on the surface.
    ''' </summary>
    Private Function SnapToSurface(dev As E3D.Placement3D) As String
        Dim src = SourcePoint(dev)
        If Not src.HasValue Then Return ""
        Dim ps = _toLocal.Transform(src.Value)
        Dim dz = -ps.Z
        If Math.Abs(dz) < 0.01 OrElse Math.Abs(dz) > 200 Then Return ""
        MoveLocal(dev, 0, 0, dz)
        Return String.Format(", mounting point on the surface ({0:0.#} mm)", dz)
    End Function

    ''' <summary>World position of the device's own mounting point: its base mate, else "G1", else the first.</summary>
    Private Function SourcePoint(dev As E3D.Placement3D) As Point3D?
        Try
            Dim arr = dev.GetSourceMates(E3D.Mate.Enums.PlacementOptions.None)
            If arr Is Nothing OrElse arr.Length = 0 Then Return Nothing
            Dim mates = arr.Cast(Of E3D.Mate)().ToList()
            Dim m As E3D.Mate = mates.FirstOrDefault(Function(x) x.IsBaseMate)
            If m Is Nothing Then m = mates.FirstOrDefault(Function(x) String.Equals(x.Name, "G1", StringComparison.OrdinalIgnoreCase))
            If m Is Nothing Then m = mates(0)
            Return MatePoint(m, dev)
        Catch
            Return Nothing
        End Try
    End Function

    ''' <summary>World position of the rail's line mate ("R1", else the first line mate, else the first).</summary>
    Private Function TargetPoint(rail As E3D.Placement3D) As Point3D?
        Try
            Dim arr = rail.GetTargetMates(False)
            If arr Is Nothing OrElse arr.Length = 0 Then Return Nothing
            Dim mates = arr.Cast(Of E3D.Mate)().ToList()
            Dim m As E3D.Mate = mates.FirstOrDefault(Function(x) String.Equals(x.Name, "R1", StringComparison.OrdinalIgnoreCase))
            If m Is Nothing Then m = mates.FirstOrDefault(Function(x) TypeOf x Is E3D.LineMate)
            If m Is Nothing Then m = mates(0)
            Return MatePoint(m, rail)
        Catch
            Return Nothing
        End Try
    End Function

    ''' <summary>
    ''' A mate's origin in world coordinates. The API documents Mate.Transformation both as relative to its placement
    ''' and (for a stored mate) as absolute in the installation space: the reading that lies on the part is used.
    ''' </summary>
    Private Shared Function MatePoint(m As E3D.Mate, owner As E3D.Placement3D) As Point3D?
        Dim t = m.Transformation
        ' the part's own geometry; a part whose geometry lies in its children (the HMI 6AV2128-3UB40 with its PC box)
        ' has an empty box of its own
        Dim box = owner.GetBoundingBox(False)
        If box.IsEmpty Then box = owner.GetBoundingBox(True)
        If box.IsEmpty Then Return Nothing
        Dim candidates As New List(Of Point3D)()
        Dim rel = Matrix3D.Multiply(t, owner.AbsoluteTransformation)
        candidates.Add(rel.Transform(New Point3D(0, 0, 0)))
        candidates.Add(t.Transform(New Point3D(0, 0, 0)))
        Try
            Dim sp = owner.InstallationSpace
            If sp IsNot Nothing Then candidates.Add(Matrix3D.Multiply(t, sp.AbsoluteTransformation).Transform(New Point3D(0, 0, 0)))
        Catch
        End Try
        For Each p In candidates
            If p.X >= box.X - 25 AndAlso p.X <= box.X + box.SizeX + 25 AndAlso
               p.Y >= box.Y - 25 AndAlso p.Y <= box.Y + box.SizeY + 25 AndAlso
               p.Z >= box.Z - 25 AndAlso p.Z <= box.Z + box.SizeZ + 25 Then Return p
        Next
        Return Nothing
    End Function

    ''' <summary>The object's bounding box in the plane's coordinate system.</summary>
    Private Function LocalBox(obj As E3D.Placement3D, withChildren As Boolean) As Rect3D
        Return ToLocal(obj.GetBoundingBox(withChildren), _toLocal)
    End Function

    Private Shared Function ToLocal(r As Rect3D, m As Matrix3D) As Rect3D
        If r.IsEmpty Then Return r
        Dim minX = Double.MaxValue, minY = Double.MaxValue, minZ = Double.MaxValue
        Dim maxX = Double.MinValue, maxY = Double.MinValue, maxZ = Double.MinValue
        For i = 0 To 7
            Dim p As New Point3D(r.X + If((i And 1) <> 0, r.SizeX, 0), r.Y + If((i And 2) <> 0, r.SizeY, 0), r.Z + If((i And 4) <> 0, r.SizeZ, 0))
            Dim q = m.Transform(p)
            minX = Math.Min(minX, q.X) : maxX = Math.Max(maxX, q.X)
            minY = Math.Min(minY, q.Y) : maxY = Math.Max(maxY, q.Y)
            minZ = Math.Min(minZ, q.Z) : maxZ = Math.Max(maxZ, q.Z)
        Next
        Return New Rect3D(minX, minY, minZ, maxX - minX, maxY - minY, maxZ - minZ)
    End Function

    ''' <summary>Size of the plane's surface (for the scale shown in the dialog): mounting surface, else the panel's outline.</summary>
    Public Shared Function SurfaceOnPlane(panel As E3D.Placement3D, plane As E3D.Plane) As PointD
        Dim s = SurfaceSize(plane)
        If s.X <= 1 OrElse s.Y <= 1 Then
            Try
                Dim m = plane.AbsoluteTransformation
                m.Invert()
                Dim b = ToLocal(panel.GetBoundingBox(False), m)
                s = New PointD(b.SizeX, b.SizeY)
            Catch
                Return New PointD(0, 0)
            End Try
        End If
        ' width x height seen from the front: the plane's x may run up
        Try
            Dim xw = plane.AbsoluteTransformation.Transform(New Vector3D(1, 0, 0))
            xw.Normalize()
            If Math.Abs(xw.Z) > 0.7 Then Return New PointD(s.Y, s.X)
        Catch
        End Try
        Return s
    End Function

    Private Structure P2
        Public X As Double
        Public Y As Double

        Public Sub New(x As Double, y As Double)
            Me.X = x
            Me.Y = y
        End Sub
    End Structure
End Class
