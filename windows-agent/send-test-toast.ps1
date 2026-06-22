# Raise a test Windows toast so you can verify the bridge end-to-end.
# Run with Windows PowerShell 5.1 (WinRT type loading does not work in pwsh 7):
#   powershell.exe -ExecutionPolicy Bypass -File .\send-test-toast.ps1
[void][Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]
$x = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent(
    [Windows.UI.Notifications.ToastTemplateType]::ToastText02)
$t = $x.GetElementsByTagName('text')
[void]$t.Item(0).AppendChild($x.CreateTextNode('NotifyBridge Test'))
[void]$t.Item(1).AppendChild($x.CreateTextNode('Windows to Linux'))
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier(
    'Microsoft.Windows.Shell.RunDialog').Show(
    [Windows.UI.Notifications.ToastNotification]::new($x))
Write-Host 'toast sent'
