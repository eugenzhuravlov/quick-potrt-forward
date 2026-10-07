# Запуск с повышением привилегий (UAC) для регистрации источника "PortForwarder" в журнале Application
param([switch]$Elevated)

function Test-Admin {
    $currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Admin)) {
    Write-Host "Запрос прав администратора (UAC)..." -ForegroundColor Yellow
    $scriptPath = $MyInvocation.MyCommand.Path
    if (-not $scriptPath) {
        $scriptPath = (Resolve-Path ".\register-eventsource.ps1").Path
    }
    
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`" -Elevated" -Wait
    exit
}

try {
    $keyPath = "HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\Application\PortForwarder"
    if (-not (Test-Path $keyPath)) {
        New-EventLog -LogName "Application" -Source "PortForwarder"
        Write-Host "Источник 'PortForwarder' успешно зарегистрирован в журнале 'Application'!" -ForegroundColor Green
    } else {
        Write-Host "Источник 'PortForwarder' уже зарегистрирован." -ForegroundColor Yellow
    }
} catch {
    Write-Error "Ошибка при регистрации источника: $_"
}

if ($Elevated) {
    Start-Sleep -Seconds 2
}
