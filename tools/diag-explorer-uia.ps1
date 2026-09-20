# 探测 Explorer 地址栏的 UIA 结构（第二版）：不限 Edit，全树扫描值像路径的控件。
# Win10 Explorer 的地址栏不是 ControlType.Edit（实测 Edit 数=0），所以要放宽条件看真实结构。

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ClassNameProperty, 'CabinetWClass')
$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
Write-Output ("Explorer windows = " + $wins.Count)
if ($wins.Count -eq 0) { exit 0 }

$w = $wins.Item(0)
Write-Output ("title = " + $w.Current.Name)

$all = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                  [System.Windows.Automation.Condition]::TrueCondition)
Write-Output ("descendants = " + $all.Count)

for ($i = 0; $i -lt $all.Count; $i++) {
    $e = $all.Item($i)
    $id = $e.Current.AutomationId
    $ct = $e.Current.ControlType.ProgrammaticName
    if ($id -match '^\d+$' -or $id -match 'Edit|ComboBox|Address') {
        $val = ""
        try {
            $v = $e.GetCurrentPattern([System.Windows.Automation.ValuePattern])
            $val = $v.Current.Value
        } catch {
            try {
                $v2 = $e.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern])
                $val = "(ExpandCollapse)"
            } catch { $val = "(no-pattern) " + $e.Current.Name }
        }
        if ($id -match '^(1148|1001|2000|40965|Address)' -or $val -match '\\') {
            Write-Output ("  " + $ct + " AutomationId=" + $id + " Value=" + $val)
        }
    }
}
