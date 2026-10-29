Imports System.Threading
Imports System.Threading.Tasks
Public Module Upstream
    Public OAuthClientId As String
    Public HasRunningMinecraft As Boolean
    Public McLaunchProcess As Diagnostics.Process
    Public McWatcherList As New List(Of Watcher)
    Public McLaunchLoader As New Loader
    Public Class Loader
        Public State As LoadState
    End Class
    Public Enum LoadState
        Waiting
        Loading
    End Enum
    Public Class Watcher
        Public GameProcess As Diagnostics.Process
        Public State As MinecraftState
        Public Enum MinecraftState
            Loading
            Running
            Ended
        End Enum
    End Class
    Public Class McInstance
        Public Sub New(path As String)
        End Sub
    End Class
    Public Class McInstanceLock
        Public Shared Function TryAcquire(worker As MeloongCore.RedoableWorker, root As String) As Boolean
            Return True
        End Function
    End Class
End Module
Namespace MeloongCore
    Public Class RedoableWorker
        Private ReadOnly Work As Action
        Public Event Succeeded()
        Public Event Failed(ex As Exception)
        Public Event Canceled()
        Public Event Stopped()
        Public Sub New(action As Action)
            Work = action
        End Sub
        Public Sub Start()
            Task.Run(Sub()
                Try
                    Work()
                    RaiseEvent Succeeded()
                Catch ex As Exception
                    RaiseEvent Failed(ex)
                Finally
                    RaiseEvent Stopped()
                End Try
            End Sub)
        End Sub
    End Class
End Namespace
