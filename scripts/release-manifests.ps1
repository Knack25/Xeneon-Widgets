$PlannerWidgetManifest = @(
    'index.html',
    'manifest.json',
    'styles.css',
    'resources/icon.svg',
    'src/api.js',
    'src/app.js',
    'src/filters.js',
    'src/state.js',
    'src/view-state.js'
)

$PlannerConnectionTestManifest = @(
    'app.js',
    'index.html',
    'manifest.json',
    'resources/icon.svg'
)

$OutlookWidgetManifest = @(
    'index.html',
    'manifest.json',
    'translation.json',
    'translations/en.json',
    'resources/icon.svg',
    'assets/app.js',
    'assets/app.css',
    'THIRD-PARTY-NOTICES.txt'
)

$HelperPublishManifest = @(
    'appsettings.Development.json',
    'appsettings.json',
    'MicrosoftWidgets.Helper.exe',
    'MicrosoftWidgets.Helper.pdb',
    'MicrosoftWidgets.Helper.staticwebassets.endpoints.json',
    'web.config'
)

$HelperStaticAssets = @(
    'wwwroot/helper-api.js',
    'wwwroot/helper.ico',
    'wwwroot/index.html',
    'wwwroot/navigation.js',
    'wwwroot/outlook-setup.js',
    'wwwroot/setup.css',
    'wwwroot/setup.js',
    'wwwroot/updates.js',
    'wwwroot/board/index.html',
    'wwwroot/board/styles.css',
    'wwwroot/board/resources/icon.svg',
    'wwwroot/board/src/api.js',
    'wwwroot/board/src/app.js',
    'wwwroot/board/src/filters.js',
    'wwwroot/board/src/state.js',
    'wwwroot/board/src/view-state.js',
    'wwwroot/outlook/index.html',
    'wwwroot/outlook/manifest.json',
    'wwwroot/outlook/translation.json',
    'wwwroot/outlook/translations/en.json',
    'wwwroot/outlook/resources/icon.svg',
    'wwwroot/outlook/assets/app.js',
    'wwwroot/outlook/assets/app.css',
    'wwwroot/outlook/THIRD-PARTY-NOTICES.txt'
)

foreach ($asset in $HelperStaticAssets) {
    $HelperPublishManifest += $asset, "$asset.br", "$asset.gz"
}
