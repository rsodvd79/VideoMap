pub fn apply() {
    use objc2::{AllocAnyThread, MainThreadMarker};
    use objc2_app_kit::{NSApplication, NSImage};
    use objc2_foundation::NSData;
    let Some(main_thread) = MainThreadMarker::new() else {
        return;
    };
    let data = NSData::with_bytes(include_bytes!("../assets/AppIcon.png"));
    if let Some(icon) = NSImage::initWithData(NSImage::alloc(), &data) {
        // SAFETY: AppKit is initialized by eframe and this marker proves the main thread.
        unsafe {
            NSApplication::sharedApplication(main_thread).setApplicationIconImage(Some(&icon));
        }
    }
}
