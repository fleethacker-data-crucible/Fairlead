@{
    RootModule        = 'Fairlead.psm1'
    ModuleVersion     = '0.1.0'
    GUID              = '8c3e1f4a-2b7d-4e91-a6c0-5d9f2e7b1a32'
    Author            = 'Anthony'
    Description       = 'Fairlead launcher: compiles the engine from source, starts the local model gateway on 127.0.0.1:8776 with a tray icon, and opens the one-time setup in the browser.'
    PowerShellVersion = '5.1'
    CompatiblePSEditions = @('Desktop', 'Core')
    FunctionsToExport = @(
        'Get-FairleadRoot',
        'Import-FairleadCore',
        'Initialize-FairleadEnvironment',
        'Start-Fairlead',
        'Get-FairleadStatus'
    )
    CmdletsToExport   = @()
    VariablesToExport = @()
    AliasesToExport   = @()
    PrivateData = @{ PSData = @{ Tags = @('llm', 'router', 'gateway', 'ollama', 'openai', 'anthropic') } }
}
