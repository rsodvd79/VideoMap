fn main() {
    println!("cargo:rerun-if-env-changed=VIDEOMAP_VERSION");
    let version = std::env::var("VIDEOMAP_VERSION")
        .unwrap_or_else(|_| format!("v{}", env!("CARGO_PKG_VERSION")));
    let numeric = version.strip_prefix('v').expect("Versione: usare v1.2.3");
    assert!(
        numeric
            .split('.')
            .all(|part| !part.is_empty() && part.bytes().all(|c| c.is_ascii_digit())),
        "Versione: usare v1.2.3"
    );
    println!("cargo:rustc-env=VIDEOMAP_VERSION={version}");
    #[cfg(windows)]
    {
        let mut resource = winresource::WindowsResource::new();
        resource.set_icon("assets/AppIcon.ico");
        resource.set("ProductName", "VideoMap");
        resource.set("FileDescription", "VideoMap Designer");
        resource.set("ProductVersion", numeric);
        resource.set("FileVersion", numeric);
        let mut parts = numeric
            .split('.')
            .map(|part| part.parse::<u64>().unwrap_or(u64::MAX).min(65535));
        let mut packed = 0;
        for _ in 0..4 {
            packed = (packed << 16) | parts.next().unwrap_or_default();
        }
        resource.set_version_info(winresource::VersionInfo::FILEVERSION, packed);
        resource.set_version_info(winresource::VersionInfo::PRODUCTVERSION, packed);
        if let Err(error) = resource.compile() {
            panic!("Impossibile compilare l'icona Windows: {error}");
        }
    }
}
