//! C ABI over `vectorcraft-trace` for AresToys. One call turns an RGBA raster into a finished SVG.
//!
//! Ownership: the SVG buffer returned through `out_svg` / `out_len` is a leaked `Box<[u8]>` that the
//! caller must hand back to [`vc_free`] with the same pointer and length. Nothing else crosses the
//! boundary with an owner: `rgba`, `params_json` and `swatches_rgb` are only read during the call.
//!
//! Threading: the progress callback may be invoked from a worker thread of the tracer (not the
//! calling thread), never concurrently with a later call of the same trace.

use std::ffi::c_void;
use std::fmt::Write as _;
use std::panic::{AssertUnwindSafe, catch_unwind};

use vectorcraft_trace::{Raster, TraceError, TraceParams, TraceResult, trace_watched};

const OK: i32 = 0;
const CANCELLED: i32 = 1;
const BAD_PARAMS: i32 = 2;
const BAD_RASTER: i32 = 3;
const BAD_PALETTE: i32 = 4;
const TOO_COMPLEX: i32 = 5;
const NO_PATHS: i32 = 6;
const PANIC: i32 = 7;
const NOT_FLAT: i32 = 8;

type ProgressFn = extern "C" fn(ctx: *mut c_void, done: u64, total: u64) -> i32;

/// The caller's opaque context pointer. The caller owns it and promises its callback is safe to call
/// from any thread, which is what lets the tracer's `Sync` progress closure carry it.
#[derive(Clone, Copy)]
struct Ctx(*mut c_void);

// SAFETY: the pointer is never dereferenced here, only handed back to the caller's callback.
unsafe impl Send for Ctx {}
unsafe impl Sync for Ctx {}

impl Ctx {
    fn get(self) -> *mut c_void {
        self.0
    }
}

fn status_of(error: &TraceError) -> i32 {
    match error {
        TraceError::Cancelled => CANCELLED,
        TraceError::Decode(_) | TraceError::TooLarge { .. } | TraceError::Empty => BAD_RASTER,
        TraceError::BadPalette(_) => BAD_PALETTE,
        TraceError::TooComplex { .. } => TOO_COMPLEX,
        TraceError::NotFlat { .. } => NOT_FLAT,
    }
}

fn hex_upper(c: [u8; 3]) -> String {
    format!("{:02X}{:02X}{:02X}", c[0], c[1], c[2])
}

/// Builds the SVG text. Returns `None` when no path survived (status `NO_PATHS`).
fn write_svg(result: &TraceResult, width: u32, height: u32, group_layers: bool) -> Option<String> {
    let mut svg = String::new();
    let _ = writeln!(
        svg,
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {width} {height}\" width=\"{width}\" height=\"{height}\">"
    );
    let mut written = 0usize;
    // Colour of the open <g>, if any, and how many groups each colour has already opened (ids stay unique).
    let mut open: Option<[u8; 3]> = None;
    let mut opened: Vec<([u8; 3], u32)> = Vec::new();
    for p in &result.paths {
        let bez = p.path.to_bezpath();
        if bez.elements().is_empty() {
            continue;
        }
        if group_layers && open != Some(p.color) {
            if open.is_some() {
                svg.push_str("</g>\n");
            }
            let n = match opened.iter_mut().find(|(c, _)| *c == p.color) {
                Some((_, n)) => {
                    *n += 1;
                    *n
                }
                None => {
                    opened.push((p.color, 1));
                    1
                }
            };
            if n == 1 {
                let _ = writeln!(svg, "<g id=\"layer-{}\">", hex_upper(p.color));
            } else {
                let _ = writeln!(svg, "<g id=\"layer-{}-{n}\">", hex_upper(p.color));
            }
            open = Some(p.color);
        }
        let d = bez.to_svg();
        let hex = hex_upper(p.color);
        match p.stroke {
            Some(w) => {
                let cap = if p.square_caps { "butt" } else { "round" };
                let _ = writeln!(
                    svg,
                    "<path d=\"{d}\" fill=\"none\" stroke=\"#{hex}\" stroke-width=\"{w:.3}\" stroke-linecap=\"{cap}\" stroke-linejoin=\"round\"/>"
                );
            }
            None => {
                let _ = writeln!(svg, "<path d=\"{d}\" fill=\"#{hex}\"/>");
            }
        }
        written += 1;
    }
    if open.is_some() {
        svg.push_str("</g>\n");
    }
    svg.push_str("</svg>\n");
    (written > 0).then_some(svg)
}

#[allow(clippy::too_many_arguments)]
unsafe fn trace_svg(
    rgba: *const u8,
    width: u32,
    height: u32,
    params_json: *const u8,
    params_len: usize,
    swatches_rgb: *const u8,
    swatch_count: usize,
    max_anchors: usize,
    group_layers: i32,
    progress: Option<ProgressFn>,
    ctx: *mut c_void,
    out_svg: *mut *mut u8,
    out_len: *mut usize,
) -> i32 {
    // SAFETY: the caller promises every non-null pointer is valid for the stated length.
    unsafe {
        if params_json.is_null() {
            return BAD_PARAMS;
        }
        let json = std::slice::from_raw_parts(params_json, params_len);
        let Ok(mut params) = serde_json::from_slice::<TraceParams>(json) else {
            return BAD_PARAMS;
        };

        if swatch_count > 0 {
            let Some(bytes) = swatch_count.checked_mul(3) else {
                return BAD_PALETTE;
            };
            if swatches_rgb.is_null() {
                return BAD_PALETTE;
            }
            params.swatches = std::slice::from_raw_parts(swatches_rgb, bytes)
                .chunks_exact(3)
                .map(|c| [c[0], c[1], c[2]])
                .collect();
        }

        if rgba.is_null() || width == 0 || height == 0 {
            return BAD_RASTER;
        }
        let Some(len) = (width as usize).checked_mul(height as usize).and_then(|n| n.checked_mul(4)) else {
            return BAD_RASTER;
        };
        let raster = match Raster::new(width, height, std::slice::from_raw_parts(rgba, len).to_vec()) {
            Ok(r) => r,
            Err(e) => return status_of(&e),
        };

        let ctx = Ctx(ctx);
        let steps = move |done: u64, total: u64| match progress {
            Some(cb) => cb(ctx.get(), done, total) != 0,
            None => true,
        };
        let max = if max_anchors == 0 { usize::MAX } else { max_anchors };
        let result = match trace_watched(&raster, &params, max, &steps) {
            Ok(r) => r,
            Err(e) => return status_of(&e),
        };
        let Some(svg) = write_svg(&result, width, height, group_layers != 0) else {
            return NO_PATHS;
        };

        let boxed = svg.into_bytes().into_boxed_slice();
        let n = boxed.len();
        *out_len = n;
        *out_svg = Box::into_raw(boxed).cast::<u8>();
        OK
    }
}

/// Traces a straight (unpremultiplied) RGBA8 raster, top row first, and returns the SVG.
///
/// Returns 0 ok, 1 cancelled, 2 bad params JSON, 3 bad raster, 4 bad palette, 5 too complex,
/// 6 no paths, 7 panic, 8 not flat. On 0 only, `*out_svg` / `*out_len` hold a buffer to release with
/// [`vc_free`]; on any other status they are null / 0.
///
/// # Safety
/// `rgba` must point at `width * height * 4` readable bytes, `params_json` at `params_len` bytes,
/// `swatches_rgb` at `swatch_count * 3` bytes (may be null when the count is 0), and `out_svg` /
/// `out_len` must be valid for writes. `progress`, when given, returns 0 to cancel.
#[unsafe(no_mangle)]
#[allow(clippy::too_many_arguments)]
pub unsafe extern "C" fn vc_trace_svg(
    rgba: *const u8,
    width: u32,
    height: u32,
    params_json: *const u8,
    params_len: usize,
    swatches_rgb: *const u8,
    swatch_count: usize,
    max_anchors: usize,
    group_layers: i32,
    progress: Option<ProgressFn>,
    ctx: *mut c_void,
    out_svg: *mut *mut u8,
    out_len: *mut usize,
) -> i32 {
    if out_svg.is_null() || out_len.is_null() {
        return BAD_RASTER;
    }
    // SAFETY: both out pointers were checked non-null; the caller promises they are writable.
    unsafe {
        *out_svg = std::ptr::null_mut();
        *out_len = 0;
    }
    catch_unwind(AssertUnwindSafe(|| unsafe {
        trace_svg(
            rgba, width, height, params_json, params_len, swatches_rgb, swatch_count, max_anchors, group_layers,
            progress, ctx, out_svg, out_len,
        )
    }))
    .unwrap_or(PANIC)
}

/// Releases a buffer returned by [`vc_trace_svg`]. Null is ignored.
///
/// # Safety
/// `ptr` / `len` must be exactly the pair a successful `vc_trace_svg` returned, freed once.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn vc_free(ptr: *mut u8, len: usize) {
    if ptr.is_null() {
        return;
    }
    // SAFETY: the pair came from `Box::into_raw` of a `Box<[u8]>` of this length. Freeing a byte
    // slice runs no user code, so nothing here can panic.
    drop(unsafe { Box::from_raw(std::ptr::slice_from_raw_parts_mut(ptr, len)) });
}
