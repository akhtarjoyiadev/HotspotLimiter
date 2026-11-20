param([ValidateSet('status','start','stop','repair')][string]$Action = 'status')
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Networking.Connectivity.NetworkInformation,Windows.Networking.Connectivity,ContentType=WindowsRuntime]
$null = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager,Windows.Networking.NetworkOperators,ContentType=WindowsRuntime]
function Await-Operation($operation, [Type]$type) {
    $method = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } | Select-Object -First 1
    $task = $method.MakeGenericMethod($type).Invoke($null, @($operation))
    if (!$task.Wait(25000)) { throw 'Windows hotspot operation timed out. Check the Wi-Fi driver and try Repair hotspot.' }
    return $task.Result
}
function Await-Action($operation) {
    $method = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and !$_.IsGenericMethod -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction' } | Select-Object -First 1
    $task = $method.Invoke($null, @($operation))
    if (!$task.Wait(25000)) { throw 'Hotspot configuration timed out.' }
    $task.GetAwaiter().GetResult()
}
$result = [ordered]@{ State='Unknown'; Success=$false; Error=''; ClientCount=0; MaxClientCount=0; Profile=''; Band=''; Ssid=''; Clients=@() }
try {
    $default = [Windows.Networking.Connectivity.NetworkInformation]::GetInternetConnectionProfile()
    $profiles = @()
    if ($null -ne $default) { $profiles += $default }
    $profiles += @([Windows.Networking.Connectivity.NetworkInformation]::GetConnectionProfiles() | Where-Object { $null -ne $_.NetworkAdapter -and $_.GetNetworkConnectivityLevel().ToString() -eq 'InternetAccess' })
    $manager = $null
    $failures = @()
    foreach ($profile in $profiles) {
        try {
            $manager = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::CreateFromConnectionProfile($profile)
            if ($null -ne $manager) { $result.Profile = $profile.ProfileName; break }
        } catch { $failures += $_.Exception.Message }
    }
    if ($null -eq $manager) { throw ('No usable internet-sharing profile. Check the Ethernet/internet connection and Wi-Fi adapter. ' + ($failures -join '; ')) }
    if ($Action -eq 'repair') {
        if ($manager.TetheringOperationalState.ToString() -ne 'Off') {
            $r = Await-Operation ($manager.StopTetheringAsync()) ([Windows.Networking.NetworkOperators.NetworkOperatorTetheringOperationResult])
            if ($r.Status.ToString() -ne 'Success') { throw ('Stop failed: ' + $r.Status + ' ' + $r.AdditionalErrorMessage) }
        }
        foreach ($name in @('WlanSvc','SharedAccess','icssvc')) {
            if ((Get-Service $name).Status -ne 'Running') { Start-Service $name }
        }
        Start-Sleep -Seconds 2
    }
    if ($Action -in @('start','repair') -and $manager.TetheringOperationalState.ToString() -ne 'On') {
        $config = $manager.GetCurrentAccessPointConfiguration()
        if ($config.Band.ToString() -ne 'TwoPointFourGigahertz') {
            $config.Band = [Windows.Networking.NetworkOperators.TetheringWiFiBand]::TwoPointFourGigahertz
            Await-Action ($manager.ConfigureAccessPointAsync($config))
        }
        $r = Await-Operation ($manager.StartTetheringAsync()) ([Windows.Networking.NetworkOperators.NetworkOperatorTetheringOperationResult])
        if ($r.Status.ToString() -ne 'Success') { throw ('Start failed: ' + $r.Status + ' ' + $r.AdditionalErrorMessage) }
        # Prevent Windows idle timeout from silently switching off the hotspot.
        try {
            Await-Action ([Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::DisableNoConnectionsTimeoutAsync())
        } catch { } # Not available on older Windows builds.
    }
    if ($Action -eq 'stop' -and $manager.TetheringOperationalState.ToString() -ne 'Off') {
        $r = Await-Operation ($manager.StopTetheringAsync()) ([Windows.Networking.NetworkOperators.NetworkOperatorTetheringOperationResult])
        if ($r.Status.ToString() -ne 'Success') { throw ('Stop failed: ' + $r.Status + ' ' + $r.AdditionalErrorMessage) }
    }
    $result.State = $manager.TetheringOperationalState.ToString()
    $result.ClientCount = [int]$manager.ClientCount
    $result.MaxClientCount = [int]$manager.MaxClientCount
    $config = $manager.GetCurrentAccessPointConfiguration()
    $result.Band = $config.Band.ToString()
    $result.Ssid = $config.Ssid
    try {
        $result.Clients = @($manager.GetTetheringClients() | ForEach-Object {
            $client = $_
            $ip = @($client.HostNames | Where-Object { $_.Type.ToString() -eq 'Ipv4' } | ForEach-Object { $_.CanonicalName }) | Select-Object -First 1
            [pscustomobject]@{ IpAddress=[string]$ip; MacAddress=[string]$client.MacAddress }
        })
    } catch { } # Client enumeration may be unsupported on older Windows builds.
    $result.Success = $true
} catch { $result.Error = $_.Exception.Message }
$result | ConvertTo-Json -Compress
