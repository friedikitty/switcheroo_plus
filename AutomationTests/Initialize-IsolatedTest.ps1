# Dot-source before launching a legacy hotkey scenario. Never touch the live app/profile.
$testId=[guid]::NewGuid().ToString('N')
$isolatedConfigRoot=Join-Path $repoRoot ('.temp\legacy-tests\'+$testId)
New-Item -ItemType Directory -Path $isolatedConfigRoot -Force|Out-Null
$env:SWITCHEROO_TEST_MODE='1'
$env:SWITCHEROO_TEST_ROOT=$isolatedConfigRoot
$env:SWITCHEROO_TEST_ID=$testId
