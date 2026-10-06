function Test-StandardApplicationArchiveName {
    param([string]$Name)
    return $Name -match '^GameValueEditor-v(0|[1-9][0-9]*)\.[0-9]\.[0-9]-win-x64\.zip$'
}
