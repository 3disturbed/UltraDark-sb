// -----------------------------------------------------------------------------
// Texture2D — an image the renderers can draw.
//
// Wraps whatever the browser gave us (an ImageBitmap, an HTMLImageElement or a
// canvas) so the 2D and 3D paths have one type to talk about, the way MonoGame's
// Texture2D does. The WebGL handle is created lazily, on the first 3D draw, so
// a purely 2D game never touches the GL context.
// -----------------------------------------------------------------------------

/** A drawable image. */
export class Texture2D {
    /**
     * @param {ImageBitmap|HTMLImageElement|HTMLCanvasElement|OffscreenCanvas} source
     * @param {string} [path] The project-relative path it was loaded from.
     */
    constructor(source, path = '') {
        this.source = source;
        this.path = path;
        this.width = source?.width ?? 0;
        this.height = source?.height ?? 0;

        /** @type {?WebGLTexture} Created on first use by the 3D renderer. */
        this.glTexture = null;
        this._glContext = null;
    }

    /**
     * The GL texture for this image, uploaded on first request.
     * @param {WebGL2RenderingContext} gl
     */
    getGLTexture(gl) {
        if (this.glTexture && this._glContext === gl) return this.glTexture;

        const texture = gl.createTexture();
        gl.bindTexture(gl.TEXTURE_2D, texture);
        gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, true);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, this.source);

        // Mipmaps need power-of-two dimensions in WebGL1; WebGL2 lifts that, but
        // clamping still avoids surprises on non-power-of-two atlases.
        gl.generateMipmap(gl.TEXTURE_2D);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR_MIPMAP_LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);

        this.glTexture = texture;
        this._glContext = gl;
        return texture;
    }

    /** Releases the GL handle. The image itself is left to the garbage collector. */
    dispose() {
        if (this.glTexture && this._glContext) {
            this._glContext.deleteTexture(this.glTexture);
            this.glTexture = null;
            this._glContext = null;
        }
    }

    /**
     * A solid-colour texture. The 2D batch uses a 1x1 white one to fill
     * rectangles, exactly as `Widget.GetPixel` does in the C# UI.
     */
    static solid(color = '#FFFFFF', width = 1, height = 1) {
        const canvas = typeof OffscreenCanvas !== 'undefined'
            ? new OffscreenCanvas(width, height)
            : Object.assign(document.createElement('canvas'), { width, height });

        const ctx = canvas.getContext('2d');
        ctx.fillStyle = typeof color === 'string' ? color : color.toCss();
        ctx.fillRect(0, 0, width, height);
        return new Texture2D(canvas, `:solid:${color}`);
    }

    /**
     * A checkerboard, used where a texture is missing so the gap is obvious
     * rather than silently black.
     */
    static missing(size = 64) {
        const canvas = typeof OffscreenCanvas !== 'undefined'
            ? new OffscreenCanvas(size, size)
            : Object.assign(document.createElement('canvas'), { width: size, height: size });

        const ctx = canvas.getContext('2d');
        const half = size / 2;
        ctx.fillStyle = '#FF00FF';
        ctx.fillRect(0, 0, size, size);
        ctx.fillStyle = '#202020';
        ctx.fillRect(0, 0, half, half);
        ctx.fillRect(half, half, half, half);
        return new Texture2D(canvas, ':missing:');
    }
}
