$events = Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Microsoft-Windows-Kernel-Power'; Id=506,507,42,107,41,109} -MaxEvents 40 -ErrorAction SilentlyContinue
$limit = (Get-Date).AddHours(-24)
foreach ($e in ($events | Sort-Object TimeCreated)) {
    if ($e.TimeCreated -lt $limit) { continue }
    Write-Output "===== {0} | ID {1} =====" -f $e.TimeCreated.ToString("dd/MM HH:mm:ss"), $e.Id
    Write-Output ($e.Message -replace "`r`n", " / ")
    Write-Output ""
}
Write-Output "--- Demarrages / arrets systeme ---"
$boot = Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Microsoft-Windows-Kernel-General'; Id=12,13} -MaxEvents 10 -ErrorAction SilentlyContinue
foreach ($e in ($boot | Sort-Object TimeCreated)) {
    if ($e.TimeCreated -lt $limit) { continue }
    Write-Output ("{0} | ID {1}" -f $e.TimeCreated.ToString("dd/MM HH:mm:ss"), $e.Id)
}
