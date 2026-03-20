# Add Inno Setup 6 to user PATH
$innoPath = 'C:\Users\black\AppData\Local\Programs\Inno Setup 6'
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if ($userPath -notlike "*Inno Setup 6*") {
    [Environment]::SetEnvironmentVariable('Path', $userPath + ';' + $innoPath, 'User')
    Write-Host 'Added Inno Setup 6 to user PATH. Open a new terminal for it to take effect.'
} else {
    Write-Host 'Inno Setup 6 is already in user PATH.'
}
