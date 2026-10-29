use anyhow::{Context, Result, bail, ensure};
use libloading::Library;
use std::{
    collections::HashMap,
    ffi::{CStr, CString, c_char, c_int, c_uint, c_void},
    path::{Path, PathBuf},
    ptr,
    sync::{Arc, Mutex, mpsc},
    thread,
    time::Duration,
};

type Handle = *mut c_void;
type Lock = unsafe extern "C" fn(Handle, *mut Handle) -> Handle;
type Unlock = unsafe extern "C" fn(Handle, Handle, *const Handle);
type Display = unsafe extern "C" fn(Handle, Handle);
type Format = unsafe extern "C" fn(
    *mut Handle,
    *mut c_char,
    *mut c_uint,
    *mut c_uint,
    *mut c_uint,
    *mut c_uint,
) -> c_uint;
type Cleanup = unsafe extern "C" fn(Handle);

macro_rules! api {
    ($($name:ident: $ty:ty),* $(,)?) => {
        struct Api { $($name: $ty,)* }
        impl Api {
            unsafe fn load(lib: &Library) -> Result<Self> {
                Ok(Self { $($name: unsafe { *lib.get::<$ty>(concat!("libvlc_",stringify!($name),"\0").as_bytes())? },)* })
            }
        }
    };
}
api! {
    new: unsafe extern "C" fn(c_int, *const *const c_char) -> Handle,
    release: unsafe extern "C" fn(Handle),
    get_version: unsafe extern "C" fn() -> *const c_char,
    media_new_path: unsafe extern "C" fn(Handle,*const c_char) -> Handle,
    media_release: unsafe extern "C" fn(Handle),
    media_add_option: unsafe extern "C" fn(Handle,*const c_char),
    media_player_new: unsafe extern "C" fn(Handle) -> Handle,
    media_player_release: unsafe extern "C" fn(Handle),
    media_player_set_media: unsafe extern "C" fn(Handle,Handle),
    media_player_play: unsafe extern "C" fn(Handle) -> c_int,
    media_player_set_pause: unsafe extern "C" fn(Handle,c_int),
    media_player_stop: unsafe extern "C" fn(Handle),
    media_player_set_position: unsafe extern "C" fn(Handle,f32),
    media_player_get_position: unsafe extern "C" fn(Handle) -> f32,
    media_player_get_state: unsafe extern "C" fn(Handle) -> c_int,
    audio_set_mute: unsafe extern "C" fn(Handle,c_int),
    video_set_callbacks: unsafe extern "C" fn(Handle,Option<Lock>,Option<Unlock>,Option<Display>,Handle),
    video_set_format_callbacks: unsafe extern "C" fn(Handle,Option<Format>,Option<Cleanup>),
}

pub struct Engine {
    api: Api,
    instance: Handle,
    _library: Library,
    pub version: String,
}

// SAFETY: LibVLC instances support concurrent player operations. The instance is
// immutable here, and Arc keeps both it and its function library alive for players.
unsafe impl Send for Engine {}
unsafe impl Sync for Engine {}

pub fn resolve(path: Option<&Path>) -> Option<PathBuf> {
    let mut roots = vec![];
    if let Some(path) = path {
        roots.push(path.to_path_buf());
    }
    #[cfg(target_os = "macos")]
    {
        roots.push(PathBuf::from("/Applications/VLC.app"));
        if let Some(home) = std::env::var_os("HOME") {
            roots.push(PathBuf::from(home).join("Applications/VLC.app"));
        }
    }
    #[cfg(target_os = "windows")]
    for key in ["ProgramFiles", "ProgramFiles(x86)"] {
        if let Some(root) = std::env::var_os(key) {
            roots.push(PathBuf::from(root).join("VideoLAN/VLC"));
        }
    }
    for root in roots {
        let dirs = [
            root.clone(),
            root.join("lib"),
            root.join("MacOS"),
            root.join("MacOS/lib"),
            root.join("Contents/MacOS"),
            root.join("Contents/MacOS/lib"),
        ];
        for dir in dirs {
            let name = if cfg!(target_os = "macos") {
                "libvlc.dylib"
            } else {
                "libvlc.dll"
            };
            if dir.join(name).is_file() {
                return Some(dir.join(name));
            }
        }
    }
    None
}

pub fn relaunch(
    path: Option<&Path>,
    force: bool,
    project: Option<&Path>,
) -> Result<Option<std::process::Child>> {
    if !cfg!(target_os = "macos") && !force {
        return Ok(None);
    }
    let Some(library) = resolve(path) else {
        return Ok(None);
    };
    let directory = library.parent().context("Cartella VLC non valida")?;
    if !force
        && std::env::var_os("VIDEOMAP_VLC_BOOTSTRAPPED").as_deref() == Some(directory.as_os_str())
    {
        return Ok(None);
    }
    let parent = directory.parent().unwrap_or(directory);
    let plugins = if directory.join("plugins").is_dir() {
        directory.join("plugins")
    } else {
        parent.join("plugins")
    };
    let mut child = std::process::Command::new(std::env::current_exe()?);
    if force {
        child.args(project);
    } else {
        child.args(std::env::args_os().skip(1));
    }
    child
        .env("VIDEOMAP_VLC_BOOTSTRAPPED", directory)
        .env("VLC_PLUGIN_PATH", plugins);
    if cfg!(target_os = "macos") {
        child
            .env("DYLD_LIBRARY_PATH", directory)
            .env("DYLD_FALLBACK_LIBRARY_PATH", directory)
            .env("VLC_DATA_PATH", parent.join("share"));
    }
    Ok(Some(
        child.spawn().context("Riavvio per configurazione VLC")?,
    ))
}

impl Engine {
    pub fn load(path: Option<&Path>) -> Result<Arc<Self>> {
        let path = resolve(path)
            .context("VLC non trovato: installa VLC 3 oppure configura il percorso")?;
        // SAFETY: only the documented LibVLC 3 ABI is resolved; handles remain owned.
        unsafe {
            #[cfg(target_os = "windows")]
            let library: Library =
                libloading::os::windows::Library::load_with_flags(&path, 0x100 | 0x1000)?.into();
            #[cfg(not(target_os = "windows"))]
            let library = Library::new(&path)
                .context("Caricamento VLC: controlla percorso e architettura")?;
            let api = Api::load(&library)?;
            let version = CStr::from_ptr((api.get_version)())
                .to_string_lossy()
                .into_owned();
            ensure!(
                version.starts_with("3."),
                "Versione VLC non supportata: {version}. E richiesta VLC 3"
            );
            let args = [
                c"--quiet".as_ptr(),
                c"--no-video-title-show".as_ptr(),
                c"--no-stats".as_ptr(),
            ];
            let instance = (api.new)(args.len() as c_int, args.as_ptr());
            ensure!(
                !instance.is_null(),
                "Inizializzazione VLC fallita: controlla i plugin"
            );
            Ok(Arc::new(Self {
                api,
                instance,
                _library: library,
                version,
            }))
        }
    }
}
impl Drop for Engine {
    fn drop(&mut self) {
        // SAFETY: all players hold an Arc, so no player remains at final release.
        unsafe {
            (self.api.release)(self.instance);
        }
    }
}

#[repr(align(32))]
#[derive(Clone, Copy)]
struct Block([u8; 32]);
struct Picture {
    storage: Vec<Block>,
    size: [u32; 2],
    pitch: u32,
}
impl Picture {
    fn bytes(&self) -> &[u8] {
        // SAFETY: Block consists solely of initialized bytes; storage covers pitch*height.
        unsafe {
            std::slice::from_raw_parts(
                self.storage[0].0.as_ptr(),
                self.pitch as usize * self.size[1] as usize,
            )
        }
    }
}
#[derive(Default)]
struct Buffers {
    size: [u32; 2],
    pitch: u32,
    latest: Option<Box<Picture>>,
    #[expect(
        clippy::vec_box,
        reason = "Stable picture pointers cross the VLC callback ABI; recycle their allocations"
    )]
    pool: Vec<Box<Picture>>,
    in_flight: HashMap<usize, Box<Picture>>,
    serial: u64,
    position: f32,
    paused: bool,
    error: Option<String>,
}
struct Callbacks(Mutex<Buffers>);

unsafe extern "C" fn format(
    opaque: *mut Handle,
    chroma: *mut c_char,
    width: *mut c_uint,
    height: *mut c_uint,
    pitches: *mut c_uint,
    lines: *mut c_uint,
) -> c_uint {
    // SAFETY: LibVLC supplies valid pointers for negotiation, before frame callbacks.
    unsafe {
        let state = &*((*opaque).cast::<Callbacks>());
        let (w, h) = (*width, *height);
        if w == 0
            || h == 0
            || w > 16384
            || h > 16384
            || u64::from(w) * u64::from(h) * 4 > 256 * 1024 * 1024
        {
            return 0;
        }
        let pitch = (w * 4).div_ceil(32) * 32;
        let Ok(mut buffers) = state.0.lock() else {
            return 0;
        };
        buffers.size = [w, h];
        buffers.pitch = pitch;
        buffers.pool.clear();
        buffers.latest = None;
        ptr::copy_nonoverlapping(c"RV32".as_ptr(), chroma, 4);
        *pitches = pitch;
        *lines = h.div_ceil(32) * 32;
        1
    }
}
unsafe extern "C" fn lock(opaque: Handle, planes: *mut Handle) -> Handle {
    // SAFETY: each lock transfers a unique picture to VLC until its display callback.
    unsafe {
        let state = &*opaque.cast::<Callbacks>();
        let Ok(mut buffers) = state.0.lock() else {
            return ptr::null_mut();
        };
        let size = buffers.size;
        let pitch = buffers.pitch;
        let mut picture = buffers.pool.pop().unwrap_or_else(|| {
            Box::new(Picture {
                storage: vec![Block([0; 32]); pitch as usize * size[1].div_ceil(32) as usize],
                size,
                pitch,
            })
        });
        *planes = picture.storage[0].0.as_mut_ptr().cast();
        let id: Handle = std::ptr::from_mut(picture.as_mut()).cast();
        buffers.in_flight.insert(id as usize, picture);
        id
    }
}
unsafe extern "C" fn unlock(_: Handle, _: Handle, _: *const Handle) {}
unsafe extern "C" fn display(opaque: Handle, picture: Handle) {
    if picture.is_null() {
        return;
    }
    // SAFETY: VLC has finished writing the picture registered by lock.
    unsafe {
        let state = &*opaque.cast::<Callbacks>();
        if let Ok(mut buffers) = state.0.lock()
            && let Some(picture) = buffers.in_flight.remove(&(picture as usize))
            && picture.size == buffers.size
        {
            if let Some(old) = buffers.latest.replace(picture)
                && buffers.pool.len() < 3
                && old.size == buffers.size
            {
                buffers.pool.push(old);
            }
            buffers.serial += 1;
        }
    }
}
unsafe extern "C" fn cleanup(_: Handle) {}

pub struct Frame {
    pub size: [u32; 2],
    pub pixels: Arc<[u8]>,
    pub serial: u64,
}

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Transport {
    Play,
    Pause,
    Stop,
}
enum Command {
    Transport(Transport),
    Seek(f32),
    Mute(bool),
    Visible(bool),
    Loop(bool),
    Shutdown,
}

pub struct Player {
    commands: mpsc::Sender<Command>,
    state: Arc<Callbacks>,
    worker: Option<thread::JoinHandle<()>>,
}

impl Player {
    pub fn new(engine: Arc<Engine>, path: PathBuf, looping: bool) -> Result<Self> {
        let path = CString::new(path.to_str().context("Percorso media non UTF-8")?)?;
        let state = Arc::new(Callbacks(Mutex::new(Buffers::default())));
        let callbacks = state.clone();
        let (tx, rx) = mpsc::channel();
        let worker = thread::Builder::new()
            .name("videomap-vlc".into())
            .spawn(move || {
                if let Err(error) = run_player(engine, path, looping, callbacks.clone(), rx)
                    && let Ok(mut state) = callbacks.0.lock()
                {
                    state.error = Some(format!("{error:#}"));
                }
            })?;
        Ok(Self {
            commands: tx,
            state,
            worker: Some(worker),
        })
    }
    pub fn transport(&self, value: Transport) {
        let _ = self.commands.send(Command::Transport(value));
    }
    pub fn seek(&self, value: f32) {
        let _ = self.commands.send(Command::Seek(value.clamp(0., 1.)));
    }
    pub fn mute(&self, value: bool) {
        let _ = self.commands.send(Command::Mute(value));
    }
    pub fn visible(&self, value: bool) {
        let _ = self.commands.send(Command::Visible(value));
    }
    pub fn looping(&self, value: bool) {
        let _ = self.commands.send(Command::Loop(value));
    }
    pub fn position(&self) -> f32 {
        self.state.0.lock().map(|s| s.position).unwrap_or_default()
    }
    pub fn paused(&self) -> bool {
        self.state.0.lock().is_ok_and(|s| s.paused)
    }
    pub fn error(&self) -> Option<String> {
        self.state.0.lock().ok().and_then(|s| s.error.clone())
    }
    pub fn frame(&self) -> Option<Frame> {
        let mut state = self.state.0.lock().ok()?;
        let picture = state.latest.take()?;
        let [w, h] = picture.size;
        let mut pixels = Vec::with_capacity(w as usize * h as usize * 4);
        for row in picture
            .bytes()
            .chunks_exact(picture.pitch as usize)
            .take(h as usize)
        {
            pixels.extend_from_slice(&row[..w as usize * 4]);
        }
        for pixel in pixels.as_chunks_mut::<4>().0 {
            pixel[3] = 255;
        }
        let frame = Frame {
            size: picture.size,
            pixels: pixels.into(),
            serial: state.serial,
        };
        if state.pool.len() < 3 && picture.size == state.size {
            state.pool.push(picture);
        }
        Some(frame)
    }
    pub fn close(mut self) {
        let _ = self.commands.send(Command::Shutdown);
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}
impl Drop for Player {
    fn drop(&mut self) {
        let _ = self.commands.send(Command::Shutdown);
    }
}

unsafe fn stop_player(api: &Api, player: Handle, state: &Callbacks) {
    // SAFETY: called only by the owning worker. LibVLC 3 stop waits for callbacks,
    // including pictures prepared but never displayed during cancellation.
    unsafe {
        (api.media_player_stop)(player);
    }
    if let Ok(mut buffers) = state.0.lock() {
        buffers.in_flight.clear();
    }
}

fn run_player(
    engine: Arc<Engine>,
    path: CString,
    mut looping: bool,
    state: Arc<Callbacks>,
    rx: mpsc::Receiver<Command>,
) -> Result<()> {
    struct Native<'a> {
        engine: &'a Engine,
        player: Handle,
        media: Handle,
        state: &'a Callbacks,
    }
    impl Drop for Native<'_> {
        fn drop(&mut self) {
            // SAFETY: stop/release waits for decoder callbacks; their state outlives Native.
            unsafe {
                stop_player(&self.engine.api, self.player, self.state);
                (self.engine.api.media_player_release)(self.player);
                (self.engine.api.media_release)(self.media);
            }
        }
    }
    // SAFETY: all native player operations occur on this worker; callback state is Arc-owned.
    unsafe {
        let api = &engine.api;
        let media = (api.media_new_path)(engine.instance, path.as_ptr());
        ensure!(!media.is_null(), "Impossibile aprire il media");
        let player = (api.media_player_new)(engine.instance);
        if player.is_null() {
            (api.media_release)(media);
            bail!("Impossibile creare il player");
        }
        let native = Native {
            engine: &engine,
            player,
            media,
            state: &state,
        };
        (api.media_add_option)(media, c":avcodec-hw=none".as_ptr());
        (api.media_player_set_media)(player, media);
        (api.video_set_callbacks)(
            player,
            Some(lock),
            Some(unlock),
            Some(display),
            Arc::as_ptr(&state).cast_mut().cast(),
        );
        (api.video_set_format_callbacks)(player, Some(format), Some(cleanup));
        ensure!((api.media_player_play)(player) == 0, "Avvio video fallito");
        let mut transport = Transport::Play;
        let mut visible = true;
        loop {
            match rx.recv_timeout(Duration::from_millis(40)) {
                Ok(Command::Shutdown) | Err(mpsc::RecvTimeoutError::Disconnected) => break,
                Ok(Command::Transport(next)) => {
                    transport = next;
                    match next {
                        Transport::Play if visible => {
                            (api.media_player_play)(player);
                            (api.media_player_set_pause)(player, 0);
                        }
                        Transport::Pause => (api.media_player_set_pause)(player, 1),
                        Transport::Stop => {
                            stop_player(api, player, &state);
                            if let Ok(mut s) = state.0.lock() {
                                s.position = 0.;
                            }
                        }
                        _ => {}
                    }
                }
                Ok(Command::Visible(next)) if next != visible => {
                    visible = next;
                    if !visible {
                        stop_player(api, player, &state);
                    } else if transport != Transport::Stop {
                        (api.media_player_play)(player);
                        if transport == Transport::Pause {
                            (api.media_player_set_pause)(player, 1);
                        }
                    }
                }
                Ok(Command::Seek(position)) => (api.media_player_set_position)(player, position),
                Ok(Command::Mute(mute)) => (api.audio_set_mute)(player, i32::from(mute)),
                Ok(Command::Loop(value)) => looping = value,
                _ => {}
            }
            let playback = (api.media_player_get_state)(player);
            if playback == 7 {
                bail!("Errore di decodifica VLC");
            }
            if visible && transport == Transport::Pause && playback == 3 {
                (api.media_player_set_pause)(player, 1);
            }
            if looping && visible && transport == Transport::Play && playback == 6 {
                stop_player(api, player, &state);
                (api.media_player_play)(player);
            }
            if let Ok(mut state) = state.0.lock() {
                state.position = (api.media_player_get_position)(player).max(0.0);
                state.paused = playback == 4;
            }
        }
        drop(native);
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn negotiate(state: &Callbacks, mut width: u32, mut height: u32) {
        let mut opaque = std::ptr::from_ref(state).cast_mut().cast();
        let mut chroma = [0; 4];
        let (mut pitch, mut lines) = (0, 0);
        // SAFETY: all ABI output pointers and the callback context remain live.
        assert_eq!(
            unsafe {
                format(
                    &mut opaque,
                    chroma.as_mut_ptr(),
                    &mut width,
                    &mut height,
                    &mut pitch,
                    &mut lines,
                )
            },
            1
        );
    }

    #[test]
    fn callbacks_publish_complete_unpadded_frames_and_discard_old_format() {
        let state = Arc::new(Callbacks(Mutex::new(Buffers::default())));
        let (commands, _) = mpsc::channel();
        let player = Player {
            commands,
            state: state.clone(),
            worker: None,
        };
        let opaque = Arc::as_ptr(&state).cast_mut().cast();
        negotiate(&state, 5, 3);
        let mut planes = ptr::null_mut();
        // SAFETY: the test follows the same lock/write/display ordering as LibVLC.
        unsafe {
            let picture = lock(opaque, &mut planes);
            assert_eq!(planes as usize % 32, 0);
            let pitch = state.0.lock().unwrap().pitch;
            ptr::write_bytes(planes.cast::<u8>(), 0x55, pitch as usize * 3);
            assert!(player.frame().is_none());
            display(opaque, picture);
            let frame = player.frame().unwrap();
            assert_eq!(frame.size, [5, 3]);
            assert_eq!(frame.pixels.len(), 5 * 3 * 4);
            assert!(
                frame
                    .pixels
                    .as_chunks::<4>()
                    .0
                    .iter()
                    .all(|p| *p == [0x55, 0x55, 0x55, 255])
            );

            let old = lock(opaque, &mut planes);
            negotiate(&state, 9, 7);
            let current = lock(opaque, &mut planes);
            display(opaque, old);
            assert!(player.frame().is_none());
            display(opaque, current);
            assert_eq!(player.frame().unwrap().size, [9, 7]);
            assert!(state.0.lock().unwrap().in_flight.is_empty());
        }
    }
}
