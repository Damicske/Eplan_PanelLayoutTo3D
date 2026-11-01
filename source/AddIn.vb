Option Strict On
Option Infer On

Imports System.Diagnostics
Imports Microsoft.VisualBasic
Imports System.Linq
Imports System.Windows.Forms
Imports Eplan.EplApi.ApplicationFramework
Imports Eplan.EplApi.DataModel
Imports Eplan.EplApi.Gui
Imports Eplan.EplApi.HEServices
Imports EFunction = Eplan.EplApi.DataModel.Function

''' <summary>
''' The add-in: registers the action <see cref="PanelLayoutTo3DAction"/> and a button for it in the ribbon
''' (Extensions &gt; API).
''' </summary>
Public Class AddInModule
    Implements IEplAddIn

    ''' <summary>
    ''' Ribbon icon (SVG on a 16 x 16 pixel grid, bold shapes only so it stays clear when EPLAN scales it): the 2D
    ''' page with an orange rail behind a blue 3D block with the same rail.
    ''' </summary>
    Friend Const IconSvg As String =
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16' width='16' height='16'>" &
        "<rect x='0' y='0' width='9' height='11' fill='#404040'/>" &
        "<rect x='1' y='1' width='7' height='9' fill='#ffffff'/><rect x='2' y='4' width='5' height='2' fill='#f08a00'/>" &
        "<polygon points='6,7 9,4 16,4 13,7' fill='#a9cbf2'/><polygon points='13,7 16,4 16,13 13,16' fill='#1d4c90'/>" &
        "<rect x='6' y='7' width='7' height='9' fill='#3b7dd8'/>" &
        "<rect x='6' y='10' width='7' height='2' fill='#f08a00'/>" &
        "<polygon points='13,10 16,7 16,9 13,12' fill='#b86a00'/></svg>"

    Public Function OnRegister(ByRef bLoadOnStart As Boolean) As Boolean Implements IEplAddIn.OnRegister
        bLoadOnStart = True
        Try
            Dim ribbon As New RibbonBar()
            Dim icon As RibbonIcon = Nothing
            Try
                icon = ribbon.AddIcon(IconSvg)
                ' EPLAN 2027 and later: a large button (RibbonIcon.IsLarge, not there in 2026)
                If icon IsNot Nothing Then CallByName(icon, "IsLarge", CallType.Let, True)
            Catch
            End Try
            If icon IsNot Nothing Then
                ribbon.AddCommand("2D layout -> 3D", PanelLayoutTo3DAction.ActionName, icon)
            Else
                ribbon.AddCommand("2D layout -> 3D", PanelLayoutTo3DAction.ActionName)
            End If
        Catch
            ' no ribbon button: the action can still be called by its name
        End Try
        Return True
    End Function

    Public Function OnUnregister() As Boolean Implements IEplAddIn.OnUnregister
        Try
            Dim ribbon As New RibbonBar()
            ribbon.RemoveCommand(PanelLayoutTo3DAction.ActionName)
        Catch
        End Try
        Return True
    End Function

    Public Function OnInit() As Boolean Implements IEplAddIn.OnInit
        Return True
    End Function

    Public Function OnInitGui() As Boolean Implements IEplAddIn.OnInitGui
        Return True
    End Function

    Public Function OnExit() As Boolean Implements IEplAddIn.OnExit
        Return True
    End Function
End Class

''' <summary>
''' Action "CdPcPanelLayoutTo3D": takes the 2D panel layout page selected in the page navigator (or open in the
''' graphical editor) and shows <see cref="FrmLayoutTo3D"/>. Objects selected on that page beforehand are the only
''' ones ticked for conversion.
''' </summary>
Public Class PanelLayoutTo3DAction
    Implements IEplAction

    Public Const ActionName As String = "CdPcPanelLayoutTo3D"

    Public Function OnRegister(ByRef Name As String, ByRef Ordinal As Integer) As Boolean Implements IEplAction.OnRegister
        Name = ActionName
        Ordinal = 20
        Return True
    End Function

#If Not EPLAN_NET8 Then
    Public Sub GetActionProperties(ByRef actionProperties As ActionProperties) Implements IEplAction.GetActionProperties
    End Sub
#End If

    Public Function Execute(ctx As ActionCallingContext) As Boolean Implements IEplAction.Execute
        Try
            Dim sel As New SelectionSet()
            sel.LockProjectByDefault = False
            Dim prj = sel.GetCurrentProject(True)
            If prj Is Nothing Then
                MessageBox.Show("Open a project first.", "2D layout -> 3D", MessageBoxButtons.OK, MessageBoxIcon.Information)
                Return False
            End If

            Dim pages = sel.GetSelectedPages()
            Dim page As Page = Nothing
            If pages IsNot Nothing AndAlso pages.Length > 0 Then
                page = pages.FirstOrDefault(Function(p) LayoutReader.IsPanelLayout(p))
                If page Is Nothing Then page = pages(0)
            End If
            If page Is Nothing Then
                MessageBox.Show("Select the 2D panel layout page in the page navigator (or open it).", "2D layout -> 3D",
                                MessageBoxButtons.OK, MessageBoxIcon.Information)
                Return False
            End If
            If Not LayoutReader.IsPanelLayout(page) Then
                If MessageBox.Show("Page " & page.Name & " is not a panel layout page (" & page.PageType.ToString() & ")." &
                                   Environment.NewLine & "Use it anyway?", "2D layout -> 3D", MessageBoxButtons.YesNo,
                                   MessageBoxIcon.Question) <> DialogResult.Yes Then Return False
            End If

            ' objects selected in the graphical editor on this page
            Dim selected As New List(Of EFunction)()
            Try
                Dim objs = sel.Selection
                If objs IsNot Nothing Then
                    For Each f In objs.OfType(Of EFunction)()
                        If f.Page IsNot Nothing AndAlso f.Page.Equals(page) Then selected.Add(f)
                    Next
                End If
            Catch
            End Try

            Using frm As New FrmLayoutTo3D(prj, page, selected)
                frm.ShowDialog(EplanWindow.Main)
            End Using
            Return True
        Catch ex As Exception
            MessageBox.Show(ex.ToString(), "2D layout -> 3D", MessageBoxButtons.OK, MessageBoxIcon.Error)
            Return False
        End Try
    End Function
End Class

''' <summary>EPLAN's main window as owner of the dialog.</summary>
Friend NotInheritable Class EplanWindow
    Implements IWin32Window

    Private ReadOnly _h As IntPtr

    Private Sub New(h As IntPtr)
        _h = h
    End Sub

    Public ReadOnly Property Handle As IntPtr Implements IWin32Window.Handle
        Get
            Return _h
        End Get
    End Property

    Public Shared ReadOnly Property Main As IWin32Window
        Get
            Return New EplanWindow(Process.GetCurrentProcess().MainWindowHandle)
        End Get
    End Property
End Class
