$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$WarningPreference = 'SilentlyContinue'
$request = [Console]::In.ReadToEnd() | ConvertFrom-Json
function Json($value) { ConvertTo-Json -InputObject $value -Depth 14 -Compress }
function Adapter($guid) {
    $parsed = [guid]::ParseExact($guid,'D')
    $matches = @(Get-NetAdapter -IncludeHidden | Where-Object { [guid]$_.InterfaceGuid -eq $parsed })
    if ($matches.Count -ne 1) { throw 'The original network adapter is missing or ambiguous.' }
    return $matches[0]
}
function Control($id,$name,$category,$value,$choices,$note,$binary=$false,$on='On',$off='Off') {
    [pscustomobject]@{Id=$id;Name=$name;Category=$category;Value=[string]$value;Choices=@($choices);Note=$note;Binary=$binary;On=$on;Off=$off;Supported=$true}
}
function Choice($value,$label) { [pscustomobject]@{Value=[string]$value;Label=[string]$label} }
function BoolValue($value) { if($value -isnot [bool]){throw 'The capability did not return a Boolean state.'};if ($value) {'On'} else {'Off'} }
function PagefileState {
    $computer=Get-CimInstance Win32_ComputerSystem
    $files=@(Get-CimInstance Win32_PageFileSetting | Sort-Object Name | ForEach-Object { [ordered]@{Name=($_.Name.Substring(0,1).ToUpperInvariant()+$_.Name.Substring(1));InitialSize=[uint32]$_.InitialSize;MaximumSize=[uint32]$_.MaximumSize} })
    Json ([ordered]@{Automatic=[bool]$computer.AutomaticManagedPagefile;Files=$files})
}
function PolicySpec($name) {
    $build=[Environment]::OSVersion.Version.Build
    $edition=(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').EditionID
    if ($edition -notmatch 'Professional|Enterprise|Education' -or (Get-CimInstance Win32_ComputerSystem).PartOfDomain) { throw 'Policy control excluded on this edition or domain-managed machine.' }
    switch ($name) {
        'dvr' { if($build -ge 22000){throw 'Microsoft documents this policy only for Windows 10.'}; @('HKLM:\SOFTWARE\Policies\Microsoft\Windows\GameDVR','AllowGameDVR','1','Game recording permission','Gaming','Disabling blocks Windows game recording/broadcasting. This is a permission policy, not the current recording state.','1','0') }
        'throttle' { @('HKLM:\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling','PowerThrottlingOff','0','Power throttling permitted','CPU','Disabling prevents Windows power throttling; can increase energy use, heat and background contention. Permitted does not mean currently throttled.','0','1') }
        'speech' { @('HKLM:\SOFTWARE\Policies\Microsoft\InputPersonalization','AllowInputPersonalization','1','Online speech services permitted','Privacy','Disabling blocks cloud speech recognition and dictation. Enabling defers to the user preference; it does not switch microphone access on.','1','0') }
        'ads' { @('HKLM:\SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo','DisabledByGroupPolicy','0','Advertising ID permitted','Privacy','Disabling prevents apps using the advertising ID. Sign out/in may be needed.','0','1') }
        default { throw 'Unknown documented policy.' }
    }
}
function GetControl($id) {
    $parts=$id.Split(':')
    switch ($parts[1]) {
        'service' {
            $allowed=@('Spooler','bthserv','SysMain','WSearch','DiagTrack','XblAuthManager','XblGameSave','XboxGipSvc','XboxNetApiSvc');if($parts[2] -cnotin $allowed){throw 'Service is outside the optional allowlist.'};$service=Get-Service -Name $parts[2];if($service.Status -notin @('Running','Stopped')){throw 'Service is transitioning or paused; wait before changing it.'};if($service.StartType -eq 'Disabled'){throw 'Service startup is disabled. Enable its startup configuration before controlling runtime state.'}
            Control $id ($service.DisplayName+' runtime') 'Services' (BoolValue ($service.Status -eq 'Running')) @((Choice On Running),(Choice Off Stopped)) 'Manual runtime control only. Stopping interrupts dependent functionality; no dependent services are forcibly stopped. Restore rechecks the saved state.' $true
        }
        'compression' {
            $state=BoolValue (Get-MMAgent).MemoryCompression
            Control $id 'Memory compression' 'Memory' $state @((Choice On Enabled),(Choice Off Disabled)) 'Compression can reduce paging at a CPU cost. Reboot may be required for full effect.' $true
        }
        'pagefile' {
            $state=PagefileState; $data=$state|ConvertFrom-Json;if(-not $data.Automatic -and @($data.Files).Count -eq 0){throw 'No pagefile is configured. Configure one in Windows before using this control.'}
            $automatic=Json ([ordered]@{Automatic=$true;Files=@($data.Files)})
            Control $id 'Pagefile configuration' 'Memory' $state @((Choice $state $(if($data.Automatic){'System managed'}else{'Current custom configuration'})),(Choice $automatic 'System managed')) 'Changes apply after restart. Custom sizes affect commit capacity and crash dumps. All previous configured pagefile entries are captured.'
        }
        'trim' {
            $text=& "$env:SystemRoot\System32\fsutil.exe" behavior query DisableDeleteNotify 2>&1 | Out-String
            if($LASTEXITCODE -ne 0 -or $text -notmatch '(?m)^\s*NTFS\s+DisableDeleteNotify\s*=\s*([01])'){throw 'NTFS deletion notification state could not be read.'}
            $state=if($Matches[1] -eq '0'){'On'}else{'Off'}
            Control $id 'NTFS TRIM notifications' 'Storage' $state @((Choice On Enabled),(Choice Off Disabled)) 'Controls Windows deletion notifications. Actual device TRIM support still depends on the storage stack.' $true
        }
        'policy' {
            $p=PolicySpec $parts[2];$key=$null;try{$key=Get-Item -LiteralPath $p[0] -ErrorAction Stop}catch [System.Management.Automation.ItemNotFoundException]{};$value='absent'
            if($key -and $key.GetValueNames() -contains $p[1]){if($key.GetValueKind($p[1]).ToString() -ne 'DWord'){throw 'Unexpected registry value type; not changing it.'};$value=[string]$key.GetValue($p[1]);if($value -notin @('0','1')){throw 'Unsupported policy value.'}}
            Control $id $p[3] $p[4] $value @((Choice $p[6] Enabled),(Choice $p[7] Disabled),(Choice absent 'Not configured')) $p[5] $true $p[6] $p[7]
        }
        'net' {
            $kind=$parts[2];$adapter=Adapter $parts[3];$name=[Management.Automation.WildcardPattern]::Escape($adapter.Name)
            if($kind -eq 'dns') {
                $dns=Get-DnsClientServerAddress -InterfaceIndex $adapter.ifIndex -AddressFamily IPv4
                $dnsKey=Get-Item -LiteralPath ('HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{'+$parts[3]+'}');$raw=$dnsKey.GetValue('NameServer','')
                $state=if([string]::IsNullOrWhiteSpace($raw)){'dhcp'}else{'static:'+(@($dns.ServerAddresses)-join ',')}
                Control $id ('IPv4 DNS · '+$name) 'Network' $state @((Choice dhcp Automatic),(Choice 'static:1.1.1.1,1.0.0.1' Cloudflare),(Choice 'static:8.8.8.8,8.8.4.4' Google),(Choice 'static:9.9.9.9,149.112.112.112' Quad9),(Choice $state 'Current configuration')) 'Changing DNS can break corporate/VPN names. IPv6 DNS is preserved. Automatic restores DHCP-supplied DNS; current DHCP addresses are not hard-coded.'
            } elseif($kind -eq 'rss') {
                $value=Get-NetAdapterRss -Name $name -ErrorAction Stop
                Control $id ('Receive Side Scaling · '+$name) 'Network' (BoolValue $value.Enabled) @((Choice On Enabled),(Choice Off Disabled)) 'RSS distributes receive work across CPUs. Driver support is required; reconfiguration may interrupt connectivity.' $true
            } elseif($kind -in @('rsc4','rsc6')) {
                $value=Get-NetAdapterRsc -Name $name -ErrorAction Stop;$state=if($kind -eq 'rsc4'){$value.IPv4Enabled}else{$value.IPv6Enabled}
                Control $id ('Receive Segment Coalescing '+$(if($kind -eq 'rsc4'){'IPv4'}else{'IPv6'})+' · '+$name) 'Network' (BoolValue $state) @((Choice On Enabled),(Choice Off Disabled)) 'Coalescing can reduce CPU cost for throughput workloads; latency effects depend on workload and driver.' $true
            } elseif($kind -eq 'prop') {
                $keyword=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($parts[4]))
                $properties=@(Get-NetAdapterAdvancedProperty -Name $name -AllProperties | Where-Object RegistryKeyword -CEQ $keyword)
                if($properties.Count -ne 1){throw 'Driver property is absent or ambiguous.'};$property=$properties[0]
                $valid=@($property.ValidRegistryValues);$labels=@($property.ValidDisplayValues)
                if($valid.Count -lt 2 -or $valid.Count -gt 256 -or $valid.Count -ne $labels.Count -or @($property.RegistryValue).Count -ne 1){throw 'Driver has not supplied an enumerable single-value control.'}
                $choices=@(for($i=0;$i -lt $valid.Count;$i++){Choice ([string]$valid[$i]) ([string]$labels[$i])})
                $on=@($choices|Where-Object Label -IEQ Enabled);$off=@($choices|Where-Object Label -IEQ Disabled);$binary=$choices.Count -eq 2 -and $on.Count -eq 1 -and $off.Count -eq 1
                Control $id ($property.DisplayName+' · '+$name) 'Network' ([string]$property.RegistryValue[0]) $choices 'Driver-reported option. Wrong values can reduce speed or connectivity. Stored configuration is verified; restart the adapter or PC to activate it. No automatic adapter restart.' $binary $(if($binary){$on[0].Value}else{''}) $(if($binary){$off[0].Value}else{''})
            } else {throw 'Unsupported network operation.'}
        }
        default {throw 'Unknown hardware operation.'}
    }
}
function SetControl($id,$expected,$target) {
    $before=GetControl $id
    if($before.Value -cne $expected){throw 'State changed since detection. No write performed.'}
    $parts=$id.Split(':')
    if($parts[1] -notin @('pagefile') -and -not($parts[1] -eq 'net' -and $parts[2] -eq 'dns') -and $target -cnotin @($before.Choices.Value)){throw 'Target is not supported by the detected control.'}
    switch($parts[1]) {
        'service' {if($target -eq 'On'){Start-Service -Name $parts[2]}else{Stop-Service -Name $parts[2]};$service=Get-Service -Name $parts[2];$service.WaitForStatus($(if($target -eq 'On'){[ServiceProcess.ServiceControllerStatus]::Running}else{[ServiceProcess.ServiceControllerStatus]::Stopped}),[TimeSpan]::FromSeconds(25))}
        'compression' {if($target -eq 'On'){Enable-MMAgent -MemoryCompression}else{Disable-MMAgent -MemoryCompression}}
        'pagefile' {
            $value=$target|ConvertFrom-Json
            if($value.Automatic -isnot [bool] -or @($value.Files).Count -gt 16){throw 'Invalid pagefile configuration.'}
            $seen=@{}
            foreach($file in $value.Files){if($file.Name -notmatch '^[A-Za-z]:\\pagefile\.sys$' -or $seen.ContainsKey($file.Name) -or [long]$file.InitialSize -lt 0 -or [long]$file.MaximumSize -lt [long]$file.InitialSize -or [long]$file.MaximumSize -gt 1048576){throw 'Invalid pagefile path or size.'};$seen[$file.Name]=$true}
            foreach($file in $value.Files){$drive=[IO.DriveInfo]::new($file.Name.Substring(0,3));if(-not $drive.IsReady -or $drive.DriveType -ne 'Fixed' -or $drive.DriveFormat -notin @('NTFS','ReFS')){throw 'Pagefiles require an accessible fixed NTFS/ReFS volume.'}}
            if(-not $value.Automatic -and @($value.Files).Count -eq 0){throw 'Disabling all pagefiles is not supported.'}
            $computer=Get-CimInstance Win32_ComputerSystem
            Set-CimInstance -InputObject $computer -Property @{AutomaticManagedPagefile=$false} | Out-Null
            foreach($existing in @(Get-CimInstance Win32_PageFileSetting)){if($existing.Name -notin @($value.Files.Name)){Remove-CimInstance -InputObject $existing}}
            foreach($file in $value.Files){$existing=@(Get-CimInstance Win32_PageFileSetting|Where-Object Name -IEQ $file.Name);if($existing.Count -eq 1){Set-CimInstance -InputObject $existing[0] -Property @{InitialSize=[uint32]$file.InitialSize;MaximumSize=[uint32]$file.MaximumSize}|Out-Null}else{New-CimInstance -ClassName Win32_PageFileSetting -Property @{Name=[string]$file.Name;InitialSize=[uint32]$file.InitialSize;MaximumSize=[uint32]$file.MaximumSize}|Out-Null}}
            Set-CimInstance -InputObject (Get-CimInstance Win32_ComputerSystem) -Property @{AutomaticManagedPagefile=[bool]$value.Automatic}|Out-Null
        }
        'trim' {& "$env:SystemRoot\System32\fsutil.exe" behavior set DisableDeleteNotify NTFS $(if($target -eq 'On'){'0'}else{'1'}) | Out-Null;if($LASTEXITCODE -ne 0){throw 'Windows refused the TRIM setting.'}}
        'policy' {$p=PolicySpec $parts[2];if($target -eq 'absent'){Remove-ItemProperty -LiteralPath $p[0] -Name $p[1] -ErrorAction SilentlyContinue}else{New-Item -Path $p[0] -Force|Out-Null;New-ItemProperty -LiteralPath $p[0] -Name $p[1] -PropertyType DWord -Value ([int]$target) -Force|Out-Null}}
        'net' {
            $adapter=Adapter $parts[3];$name=[Management.Automation.WildcardPattern]::Escape($adapter.Name)
            switch($parts[2]) {
                'dns' {
                    $object=Get-DnsClientServerAddress -InterfaceIndex $adapter.ifIndex -AddressFamily IPv4
                    if($target -eq 'dhcp'){Set-DnsClientServerAddress -InputObject $object -ResetServerAddresses}
                    else{if(-not $target.StartsWith('static:')){throw 'Invalid DNS mode.'};$servers=@($target.Substring(7).Split(','));if($servers.Count -lt 1 -or $servers.Count -gt 4){throw 'Enter 1–4 IPv4 DNS servers.'};foreach($server in $servers){$address=[Net.IPAddress]::Parse($server);if($address.AddressFamily -ne 'InterNetwork' -or $address.ToString() -cne $server -or $server -eq '0.0.0.0'){throw 'Invalid IPv4 DNS server.'}};Set-DnsClientServerAddress -InputObject $object -ServerAddresses $servers}
                }
                'rss' {Set-NetAdapterRss -Name $name -Enabled ($target -eq 'On')}
                'rsc4' {Set-NetAdapterRsc -Name $name -IPv4Enabled ($target -eq 'On')}
                'rsc6' {Set-NetAdapterRsc -Name $name -IPv6Enabled ($target -eq 'On')}
                'prop' {$keyword=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($parts[4]));Set-NetAdapterAdvancedProperty -Name $name -RegistryKeyword $keyword -RegistryValue @($target) -NoRestart}
            }
        }
    }
    $after=GetControl $id
    if($after.Value -cne $target){throw 'Windows read-back differs from the requested value; recovery is required.'}
    return $after
}
try {
    if($request.Action -eq 'maintenance'){
        switch($request.Id){
            'dns-test' {$watch=[Diagnostics.Stopwatch]::StartNew();$answer=@(Resolve-DnsName -Name $request.Argument -Type A -DnsOnly -NoHostsFile -ErrorAction Stop|Select-Object Name,Type,IPAddress);$watch.Stop();Json ([ordered]@{Action='DNS lookup';Milliseconds=$watch.Elapsed.TotalMilliseconds;Answers=$answer;Note='One resolver query; cache and resolver behavior affect this sample. Not a game latency test.'});exit 0}
            {$_ -in @('renew','release')} {
                $a=Adapter $request.Argument;$configs=@(Get-CimInstance Win32_NetworkAdapterConfiguration|Where-Object InterfaceIndex -EQ $a.ifIndex);if($configs.Count -ne 1 -or -not $configs[0].DHCPEnabled){throw 'This exact adapter is not DHCP-enabled.'};$before=$configs[0];$method=if($request.Id -eq 'renew'){'RenewDHCPLease'}else{'ReleaseDHCPLease'};$result=Invoke-CimMethod -InputObject $before -MethodName $method;if($result.ReturnValue -notin @(0,1)){throw ('Windows DHCP error '+$result.ReturnValue)}
                $after=Get-CimInstance Win32_NetworkAdapterConfiguration|Where-Object InterfaceIndex -EQ $a.ifIndex;$v4=@($after.IPAddress|Where-Object {$_ -match '^\d+\.' -and $_ -ne '0.0.0.0' -and $_ -notlike '169.254.*'});$verified=if($request.Id -eq 'renew'){$v4.Count -gt 0 -and $after.DHCPLeaseObtained -and $after.DHCPLeaseExpires -gt (Get-Date)}else{$v4.Count -eq 0};if(-not $verified){throw 'DHCP command returned, but the requested lease state was not verified. Inspect Windows network status.'}
                Json ([ordered]@{Action=$method;Verified=$true;RestartRequired=($result.ReturnValue -eq 1);Adapter=$a.Name;IPv4=$v4;LeaseObtained=$after.DHCPLeaseObtained;LeaseExpires=$after.DHCPLeaseExpires;Note='No exact lease rollback. Renew requests a lease; DHCP may assign a different address.'});exit 0
            }
            'startup-info' {
                $items=[Collections.Generic.List[object]]::new();$problems=[Collections.Generic.List[string]]::new()
                foreach($hive in @('HKCU','HKLM')){foreach($suffix in @('Run','RunOnce')){foreach($base in @('SOFTWARE\Microsoft\Windows\CurrentVersion\','SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\')){try{$path=$hive+':\'+$base+$suffix;if(Test-Path -LiteralPath $path){$key=Get-Item -LiteralPath $path;foreach($name in $key.GetValueNames()){$items.Add([ordered]@{Name=$name;Source=$path;Command=$key.GetValue($name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames);State='Registered; Windows approval may override';Publisher='Not inferred'})}}}catch{$problems.Add('A startup registry source was denied.')}}}}
                foreach($folder in @([Environment]::GetFolderPath('Startup'),[Environment]::GetFolderPath('CommonStartup'))){try{foreach($file in @(Get-ChildItem -LiteralPath $folder -File -ErrorAction Stop)){$items.Add([ordered]@{Name=$file.Name;Source='Startup folder';Path=$file.FullName;State='Registered';Publisher='Not inferred'})}}catch{$problems.Add('A startup folder was unavailable.')}}
                try{foreach($task in @(Get-ScheduledTask -ErrorAction Stop|Where-Object {@($_.Triggers|Where-Object {$_.CimClass.CimClassName -in @('MSFT_TaskBootTrigger','MSFT_TaskLogonTrigger')}).Count -gt 0}|Select-Object -First 256)){$items.Add([ordered]@{Name=$task.TaskName;Source='Scheduled task '+$task.TaskPath;State=[string]$task.State;Actions=@($task.Actions|Select-Object Execute,Arguments);Publisher='Not inferred'})}}catch{$problems.Add('Scheduled startup tasks could not be enumerated.')}
                try{foreach($package in @(Get-AppxPackage -ErrorAction Stop|Select-Object -First 256)){try{$manifest=Get-AppxPackageManifest -Package $package.PackageFullName -ErrorAction Stop;foreach($entry in @($manifest.SelectNodes('//*[local-name()="StartupTask"]'))){$items.Add([ordered]@{Name=$entry.TaskId;Source='Packaged startup: '+$package.Name;Publisher=$package.Publisher;State='Declared in package; user approval must be read in Windows Startup apps'})}}catch{$problems.Add('One package manifest was unreadable.')}}}catch{$problems.Add('Packaged startup inventory unavailable.')}
                Json ([ordered]@{Items=@($items);Limit='Scheduled tasks and packages bounded at 256 each; registrations do not imply impact or user approval.';Errors=@($problems)});exit 0
            }
            'delivery-scan' {Get-Command Delete-DeliveryOptimizationCache -ErrorAction Stop|Out-Null;$status=@(Get-DeliveryOptimizationStatus -ErrorAction Stop|Select-Object FileId,Status,FileSize,BytesFromPeers,BytesFromHttp);Json ([ordered]@{Jobs=$status;Note='Job inventory is not a complete on-disk cache size. Windows Storage reports the cleanup estimate.'});exit 0}
            'delivery-clean' {Delete-DeliveryOptimizationCache -Force -ErrorAction Stop; $after=@(Get-DeliveryOptimizationStatus -ErrorAction Stop|Select-Object FileId,Status,FileSize);Json ([ordered]@{CommandCompleted=$true;JobsAfter=$after;Note='Deletion requested through the Windows cache API. Downloads may resume. Exact recovered bytes unavailable; not claimed.'});exit 0}
            default {throw 'Unknown maintenance action.'}
        }
    }
    if($request.Action -eq 'read'){Json (GetControl $request.Id);exit 0}
    if($request.Action -eq 'write'){Json (SetControl $request.Id $request.Expected $request.Target);exit 0}
    if($request.Action -ne 'inspect'){throw 'Unsupported request.'}
    $errors=[Collections.Generic.List[string]]::new();$controls=[Collections.Generic.List[object]]::new()
    foreach($id in @('hw:compression','hw:pagefile','hw:trim','hw:policy:dvr','hw:policy:ads','hw:policy:throttle','hw:policy:speech')){try{$controls.Add((GetControl $id))}catch{$errors.Add($id+': '+$_.Exception.Message)}}
    foreach($name in @('Spooler','bthserv','SysMain','WSearch','DiagTrack','XblAuthManager','XblGameSave','XboxGipSvc','XboxNetApiSvc')){try{$controls.Add((GetControl ('hw:service:'+$name)))}catch{$errors.Add('Service '+$name+': '+$_.Exception.Message)}}
    $os=Get-CimInstance Win32_OperatingSystem;$cpu=@(Get-CimInstance Win32_Processor);$system=Get-CimInstance Win32_ComputerSystem
    $mobile=$null;if($system.PCSystemType -eq 2){$mobile=$true}elseif($system.PCSystemType -eq 1){$mobile=$false}
    $active=$null;$netInfo=$null
    try {
        $routes=@(Get-NetRoute -DestinationPrefix '0.0.0.0/0' -AddressFamily IPv4|Where-Object {$_.State -ne 'Dead'}|Sort-Object @{Expression={$_.RouteMetric+$_.InterfaceMetric}});if($routes.Count -gt 1 -and ($routes[0].RouteMetric+$routes[0].InterfaceMetric) -eq ($routes[1].RouteMetric+$routes[1].InterfaceMetric) -and $routes[0].InterfaceIndex -ne $routes[1].InterfaceIndex){throw 'Several equally preferred IPv4 default routes exist; select a single active route in Windows before tuning.'}
        foreach($route in $routes){$candidate=Get-NetAdapter -InterfaceIndex $route.InterfaceIndex -ErrorAction SilentlyContinue;if($candidate.Status -eq 'Up'){$active=$candidate;break}}
        if($active){
            $guid=([guid]$active.InterfaceGuid).ToString('D');$ip=Get-NetIPConfiguration -InterfaceIndex $active.ifIndex;$interface=Get-NetIPInterface -InterfaceIndex $active.ifIndex -AddressFamily IPv4
            $advanced=@();$power=$null
            if($active.HardwareInterface){
                foreach($kind in @('rss','rsc4','rsc6')){try{$controls.Add((GetControl ('hw:net:'+$kind+':'+$guid)))}catch{$errors.Add($kind+': '+$_.Exception.Message)}}
                try{$advanced=@(Get-NetAdapterAdvancedProperty -Name ([Management.Automation.WildcardPattern]::Escape($active.Name)) -AllProperties|Select-Object DisplayName,RegistryKeyword,RegistryValue,ValidDisplayValues,ValidRegistryValues)
                    foreach($property in $advanced){if($property.DisplayName -and @($property.ValidRegistryValues).Count -ge 2 -and -not($property.RegistryKeyword -eq '*RSS' -and @($controls | Where-Object Id -EQ ('hw:net:rss:'+$guid)).Count -gt 0)){try{$key=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($property.RegistryKeyword));$controls.Add((GetControl ('hw:net:prop:'+$guid+':'+$key)))}catch{$errors.Add('Driver property: '+$_.Exception.Message)}}}
                }catch{$errors.Add('Advanced adapter properties unavailable.')}
                try{$power=Get-NetAdapterPowerManagement -Name ([Management.Automation.WildcardPattern]::Escape($active.Name))|Select-Object AllowComputerToTurnOffDevice,WakeOnMagicPacket,WakeOnPattern,DeviceSleepOnDisconnect,SelectiveSuspend}catch{$errors.Add('Adapter power management unavailable.')}
            }
            try{$controls.Add((GetControl ('hw:net:dns:'+$guid)))}catch{$errors.Add('DNS: '+$_.Exception.Message)}
            $wifi=$null;if([string]$active.PhysicalMediaType -match '802.11|Wireless'){$wifi=(& "$env:SystemRoot\System32\netsh.exe" wlan show drivers 2>&1 | Out-String);if($LASTEXITCODE -ne 0){$wifi='Wi-Fi driver capability query failed.'};if($wifi.Length -gt 16000){$wifi=$wifi.Substring(0,16000)}}
            $netInfo=[ordered]@{WifiCapabilities=$wifi;ReceiveLinkBitsPerSecond=$active.ReceiveLinkSpeed;TransmitLinkBitsPerSecond=$active.TransmitLinkSpeed;Guid=$guid;Name=$active.Name;Description=$active.InterfaceDescription;Medium=[string]$active.PhysicalMediaType;Hardware=[bool]$active.HardwareInterface;LinkSpeed=[string]$active.LinkSpeed;Driver=[string]$active.DriverVersionString;Dhcp=[string]$interface.Dhcp;Mtu=$interface.NlMtu;Gateway=@($ip.IPv4DefaultGateway.NextHop);IPv4=@($ip.IPv4Address.IPAddress);IPv6=@($ip.IPv6Address.IPAddress);Dns=@($ip.DNSServer.ServerAddresses);Advanced=$advanced;Power=$power}
        }
    }catch{$errors.Add('Network detection: '+$_.Exception.Message)}
    $disks=@();$volumes=@();$tcp=@();$graphics=@();$pageUsage=@()
    try{$disks=@(Get-PhysicalDisk|Select-Object FriendlyName,MediaType,BusType,Size,HealthStatus,OperationalStatus)}catch{$errors.Add('Physical disk classification unavailable.')}
    try{$volumes=@(Get-Volume|Where-Object DriveLetter|Select-Object DriveLetter,FileSystem,Size,SizeRemaining,HealthStatus)}catch{$errors.Add('Volume information unavailable.')}
    try{$tcp=@(Get-NetTCPSetting|Select-Object SettingName,AutoTuningLevelLocal,CongestionProvider,EcnCapability)}catch{$errors.Add('TCP configuration unavailable.')}
    try{$graphics=@(Get-CimInstance Win32_VideoController|Select-Object Name,AdapterCompatibility,DriverVersion,CurrentRefreshRate,CurrentHorizontalResolution,CurrentVerticalResolution)}catch{$errors.Add('Graphics inventory unavailable.')}
    try{$pageUsage=@(Get-CimInstance Win32_PageFileUsage|Select-Object Name,AllocatedBaseSize,CurrentUsage,PeakUsage)}catch{$errors.Add('Pagefile usage unavailable.')}
    Json ([ordered]@{Build=[int]$os.BuildNumber;OS=$os.Caption;Laptop=$mobile;Cpu=@($cpu|Select-Object Manufacturer,Name,NumberOfCores,NumberOfLogicalProcessors,Architecture,CurrentClockSpeed,MaxClockSpeed);Threads=[int](($cpu|Measure-Object NumberOfLogicalProcessors -Sum).Sum);TotalMemory=[long]$os.TotalVisibleMemorySize*1024;FreeMemory=[long]$os.FreePhysicalMemory*1024;Graphics=$graphics;Network=$netInfo;Disks=$disks;Volumes=$volumes;Tcp=$tcp;Pagefiles=$pageUsage;UptimeSeconds=[long]((Get-Date)-$os.LastBootUpTime).TotalSeconds;Controls=@($controls);Errors=@($errors)})
}catch{[Console]::Error.WriteLine($_.Exception.Message);exit 1}
