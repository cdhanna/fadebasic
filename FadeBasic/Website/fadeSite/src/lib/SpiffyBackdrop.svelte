<script>
    import { onMount } from 'svelte';
    import { GatherSpiffs, GatherSpiffTexts, RenderSpiffs } from './SpiffUtil';
    import shaderString from './frag.glsl?raw'

    let canvas;
    let spiffCanvas;
    let spiffCanvasRenderFactor =.5; // TODO: text positioning isn't right
    let canvasRenderFactor = 1;
    let ctx;
    let cursorLeft = 0;
    let cursorTop = 0;

    onMount(() => {

        let spiffCtx = spiffCanvas.getContext('2d', {
            willReadFrequently: true
        });
        rerenderSpiffs(spiffCtx);
      

        // const spiffImageUrl = spiffCanvas.toDataURL();
        // const spiffImage = new Image();
        // spiffImage.src = spiffImageUrl;
        // document.body.append(spiffImage);



        // taken from some docs from Mozilla
        //  https://developer.mozilla.org/en-US/docs/Web/API/WebGL_API/By_example/Hello_GLSL
        //  https://github.com/idofilin/webgl-by-example/blob/master/hello-glsl/hello-glsl.js#L60
        ctx = canvas.getContext('webgl2', {
            preserveDrawingBuffer: true
        });
        if (!ctx) {
            alert('Hey, sorry. This website requires WebGL support, but your browser does not support it. ')
        }
        ctx.viewport(0, 0, ctx.drawingBufferWidth, ctx.drawingBufferHeight);
        ctx.clearColor(0.0, 0.0, 0.0, 1.0);
        ctx.clear(ctx.COLOR_BUFFER_BIT);


        let program = ctx.createProgram();

        const vertexShader = compileShader(ctx, `#version 300 es
                precision mediump float;

                in vec3 aVertexPosition;
                in vec2 aVertexUV;
                // uniform mediump time;

                out mediump vec2 vUV;
                void main(){
                    gl_Position = vec4(aVertexPosition, 1.0);
                    vUV = aVertexUV;
                }
            `, ctx.VERTEX_SHADER);

        const fragmentShader = compileShader(ctx, shaderString, ctx.FRAGMENT_SHADER);
        program = compileProgram(ctx, vertexShader, fragmentShader)
        ctx.useProgram(program);

        createPositions(ctx, program);
        createIndices(ctx, program);
        bindUvs(ctx, program);
        createTexture(ctx, spiffCtx)

        const timeUniform = ctx.getUniformLocation(program, "uTime");
        const frameUniform = ctx.getUniformLocation(program, "uFrame64");
        const resolutionUniform = ctx.getUniformLocation(program, "uResolution");
    
        let frame = 0;
        let startTime = Date.now();
        let count = 0;
        let needsSdf = true;
        const pixels = new Uint8Array(4 * canvas.width * canvas.height);
        const sdfPixels = new Uint8Array(4 * canvas.width * canvas.height);
        

        window.addEventListener('scroll', function(e) {
            // render();
        });
        window.addEventListener('mousemove', function(e) {
            // console.log(e.clientX, e.clientY);
            cursorLeft = e.clientX - 15;
            cursorTop = e.clientY - 15;
        });

        (function loop() {
            frame = requestAnimationFrame(loop);

            render();
        })();

        return () => {
            // do nothing
            cancelAnimationFrame(frame);
        };


        function render(){
            count ++;
            rerenderSpiffs(spiffCtx);

            // const now = count/1;//Date.now() - startTime;
            let ts = count/1000;
            // console.log(ts);
            ctx.uniform1f(timeUniform, ts);
            ctx.uniform2f(resolutionUniform, canvas.width, canvas.height);

            if (needsSdf)
            {
                { // init JFA
                    ctx.clear(ctx.COLOR_BUFFER_BIT | ctx.DEPTH_BUFFER_BIT);
                    ctx.uniform1f(frameUniform, 0);
                    ctx.texImage2D(ctx.TEXTURE_2D, 0, ctx.RGBA, ctx.RGBA, ctx.UNSIGNED_BYTE, spiffCtx.getImageData(0, 0, spiffCtx.canvas.width, spiffCtx.canvas.height));
                    ctx.drawElements(ctx.TRIANGLES, 6, ctx.UNSIGNED_SHORT, 0);  
                }
                { // run JFA 
                    let requiredSteps = Math.ceil(Math.log2(Math.max(canvas.width, canvas.height)));
                    // requiredSteps = 3;
                    for (let i = 0; i < requiredSteps; i ++){

                        ctx.readPixels(0, 0, canvas.width, canvas.height, ctx.RGBA, ctx.UNSIGNED_BYTE, pixels)
                        ctx.texImage2D(ctx.TEXTURE_2D, 0, ctx.RGBA, canvas.width, canvas.height, 0, ctx.RGBA, ctx.UNSIGNED_BYTE, pixels);
                        
                        ctx.uniform1f(frameUniform, i + 1);
                        ctx.drawElements(ctx.TRIANGLES, 6, ctx.UNSIGNED_SHORT, 0);  
                    }

                    // read and bind the sdf texture
                    ctx.readPixels(0, 0, canvas.width, canvas.height, ctx.RGBA, ctx.UNSIGNED_BYTE, sdfPixels)
                    ctx.texImage2D(ctx.TEXTURE_2D, 0, ctx.RGBA, canvas.width, canvas.height, 0, ctx.RGBA, ctx.UNSIGNED_BYTE, sdfPixels);

                    needsSdf = false;
                }
            }

            { // run FX
                ctx.uniform1f(frameUniform, 100);
                ctx.drawElements(ctx.TRIANGLES, 6, ctx.UNSIGNED_SHORT, 0);  
            }

        }
    });

    function rerenderSpiffs(spiffCtx){
        var regions = GatherSpiffs();
        var texts = GatherSpiffTexts(spiffCanvasRenderFactor);
        RenderSpiffs(spiffCanvas, spiffCtx, regions, texts, spiffCanvasRenderFactor);

        
    }

    
    // taken from 
    //  https://dev.to/ndesmic/webgl-3d-engine-from-scratch-part-1-drawing-a-colored-quad-2n48
    function compileShader(context, text, type){
        const shader = context.createShader(type);
        context.shaderSource(shader, text);
        context.compileShader(shader);

        if (!context.getShaderParameter(shader, context.COMPILE_STATUS)) {
            throw new Error(`Failed to compile shader: ${context.getShaderInfoLog(shader)}`);
        }
        return shader;
    }

    function compileProgram(context, vertexShader, fragmentShader){
        const program = context.createProgram();
        context.attachShader(program, vertexShader);
        context.attachShader(program, fragmentShader);
        context.linkProgram(program);

        if (!context.getProgramParameter(program, context.LINK_STATUS)) {
            throw new Error(`Failed to compile WebGL program: ${context.getProgramInfoLog(program)}`);
        }

        return program;
    }

    function createPositions(context, program) {
        const positionBuffer = context.createBuffer();
        context.bindBuffer(context.ARRAY_BUFFER, positionBuffer);
        const positions = new Float32Array([
            -1.0, -1.0, 1,
            1.0, -1.0, 1,
            1.0, 1.0, 1,
            -1.0, 1.0, 1
        ]);
        context.bufferData(context.ARRAY_BUFFER, positions, context.STATIC_DRAW);
        const positionLocation = context.getAttribLocation(program, "aVertexPosition");
        context.enableVertexAttribArray(positionLocation);
        context.vertexAttribPointer(positionLocation, 3, context.FLOAT, false, 0, 0);
        return positionBuffer;
    }

    function createIndices(ctx, program) {
        const indexBuffer = ctx.createBuffer();
        ctx.bindBuffer(ctx.ELEMENT_ARRAY_BUFFER, indexBuffer);
        const indices = new Uint16Array([
            0, 1, 2,
            0, 2, 3,
        ]);
        ctx.bufferData(ctx.ELEMENT_ARRAY_BUFFER, indices, ctx.STATIC_DRAW);
    }

    function bindUvs(ctx, program) {
        const uvs = new Float32Array([
            0, 0,
            1, 0,
            1, 1,
            0, 1
        ]);
        const uvBuffer = ctx.createBuffer();
        ctx.bindBuffer(ctx.ARRAY_BUFFER, uvBuffer);
        ctx.bufferData(ctx.ARRAY_BUFFER, uvs, ctx.STATIC_DRAW);
        const vertexUvLocation = ctx.getAttribLocation(program, "aVertexUV");
        ctx.enableVertexAttribArray(vertexUvLocation);
        ctx.vertexAttribPointer(vertexUvLocation, 2, ctx.FLOAT, false, 0, 0);
    }

    function createTexture(ctx, spiffContext) {
        const texture = ctx.createTexture();
        ctx.bindTexture(ctx.TEXTURE_2D, texture);
        ctx.texImage2D(ctx.TEXTURE_2D, 0, ctx.RGBA, ctx.RGBA, ctx.UNSIGNED_BYTE, spiffContext.getImageData(0, 0, spiffContext.canvas.width, spiffContext.canvas.height));
        ctx.texParameteri(ctx.TEXTURE_2D, ctx.TEXTURE_WRAP_S, ctx.CLAMP_TO_EDGE);
        ctx.texParameteri(ctx.TEXTURE_2D, ctx.TEXTURE_WRAP_T, ctx.CLAMP_TO_EDGE);
        ctx.texParameteri(ctx.TEXTURE_2D, ctx.TEXTURE_MIN_FILTER, ctx.LINEAR);
        ctx.texParameteri(ctx.TEXTURE_2D, ctx.TEXTURE_MAG_FILTER, ctx.NEAREST);
        return texture;
    }

</script>

<div>
    <canvas id="offscreen-spiffies" bind:this={spiffCanvas} width={spiffCanvasRenderFactor * window.innerWidth} height={spiffCanvasRenderFactor * window.innerHeight} />
    <canvas id="background-canvas" bind:this={canvas} width={window.innerWidth * canvasRenderFactor} height={window.innerHeight * canvasRenderFactor}></canvas>

    <!-- <div id="cursor" class="spiff" style="left: {cursorLeft}px; top: {cursorTop}px"></div> -->
</div>

<style>
    #cursor {
        position: fixed;
        width: 20px;
        height: 20px;
        border-radius: 20px;
        /* background-color: red; */
    }
    canvas#offscreen-spiffies {
        opacity: 0;
        position: fixed;
        left: 0;
        right: 0;
        z-index: 2;
        top: 0;
        bottom: 0;
        background-color:rgba(255,255,0,.3);
    }

    canvas#background-canvas{
        position: fixed;
        z-index: -1;
        left: 0;
        right: 0;
        top: 0;
        bottom: 0;
        width: 100%;
    }
    div {
        position: fixed;
        z-index: -1;
        left: 0;
        right: 0;
        top: 0;
        bottom: 0;
    }
</style>