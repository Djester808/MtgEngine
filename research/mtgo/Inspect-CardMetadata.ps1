# Read ECMA-335 metadata and PE headers without loading or running target code.
# The REA 6.7.0 parser rejects this 226 MB assembly at its 128 MiB input limit.
$ErrorActionPreference = 'Stop'
$artifactPath = Join-Path $PSScriptRoot 'artifacts/Card.dll'
$stream = [IO.File]::OpenRead($artifactPath)
$pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
try {
    $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
    $assembly = $metadata.GetAssemblyDefinition()
    $resources = @($metadata.ManifestResources | ForEach-Object {
        $resource = $metadata.GetManifestResource($_)
        [ordered]@{
            name = $metadata.GetString($resource.Name)
            offset = $resource.Offset
            attributes = [string]$resource.Attributes
            embedded = $resource.Implementation.IsNil
        }
    })
    $types = @($metadata.TypeDefinitions | ForEach-Object {
        $type = $metadata.GetTypeDefinition($_)
        [ordered]@{
            namespace = $metadata.GetString($type.Namespace)
            name = $metadata.GetString($type.Name)
            methods = @($type.GetMethods() | ForEach-Object {
                $method = $metadata.GetMethodDefinition($_)
                [ordered]@{ name = $metadata.GetString($method.Name); rva = $method.RelativeVirtualAddress }
            })
        }
    })
    $size = (Get-Item -LiteralPath $artifactPath).Length
    $resourceSize = $pe.PEHeaders.CorHeader.ResourcesDirectory.Size
    $record = [ordered]@{
        evidence_kind = 'local-pe-metadata-fallback'
        artifact = [ordered]@{
            name = 'Card.dll'
            bytes = $size
            sha256 = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
            assembly_name = $metadata.GetString($assembly.Name)
            assembly_version = [string]$assembly.Version
            mvid = [string]$metadata.GetGuid($metadata.GetModuleDefinition().Mvid)
        }
        type_count = $metadata.TypeDefinitions.Count
        method_count = $metadata.MethodDefinitions.Count
        managed_resource_bytes = $resourceSize
        managed_resource_percent = [Math]::Round(100.0 * $resourceSize / $size, 4)
        resources = $resources
        types = $types
        limitations = @(
            'Read-only PEReader metadata; no target assembly load, target execution, or resource deserialization.'
            'Resource directory size is aggregate PE metadata, not a per-entry byte measurement.'
            'Method names and RVAs are inventory facts, not behavior proof.'
            'ILSpy reconstruction was inspected separately; this record is not REA Evidence.'
        )
    }
    $record | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $PSScriptRoot 'card-metadata-fallback.json')
    [pscustomobject]$record | Select-Object artifact,type_count,method_count,managed_resource_bytes,managed_resource_percent,resources | ConvertTo-Json -Depth 5
}
finally {
    $pe.Dispose()
    $stream.Dispose()
}
