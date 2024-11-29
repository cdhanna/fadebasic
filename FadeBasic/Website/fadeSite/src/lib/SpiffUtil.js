export function GatherSpiffs(){
    var spiffs = document.querySelectorAll('.spiff');
    let regions = [];
    for (let i = 0 ; i < spiffs.length; i ++){
        let spiff = spiffs[i];

        let styleMap = spiff.computedStyleMap();
        let region = {
            x: spiff.offsetLeft,
            y: spiff.offsetTop - window.scrollY,
            width: spiff.offsetWidth,
            height: spiff.offsetHeight,
            xNormalized: spiff.offsetLeft / window.innerWidth,
            yNormalized: (spiff.offsetTop- window.scrollY) / window.innerHeight,
            widthNormalized: spiff.offsetWidth /  window.innerWidth,
            heightNormalized: spiff.offsetHeight / window.innerHeight,

            // must specify css value in px. 
            borderTopRight: styleMap.get('border-top-right-radius').value,
            borderTopLeft: styleMap.get('border-top-left-radius').value,
            borderLowRight: styleMap.get('border-bottom-right-radius').value,
            borderLowLeft: styleMap.get('border-bottom-left-radius').value
        };
        regions.push(region)
    }
    return regions;
}

export function GatherSpiffTexts(resolutionFactor){
    var texts = document.querySelectorAll('.spiff-text');
    let regions = [];
    for (let i = 0 ; i < texts.length; i ++){
        let spiff = texts[i];

        let styleMap = spiff.computedStyleMap();
        let region = {
            x: spiff.offsetLeft,
            y: spiff.offsetTop - window.scrollY,
            width: spiff.offsetWidth,
            height: spiff.offsetHeight,
            xNormalized: spiff.offsetLeft / window.innerWidth,
            yNormalized: (spiff.offsetTop- window.scrollY) / window.innerHeight,
            widthNormalized: spiff.offsetWidth /  window.innerWidth,
            heightNormalized: spiff.offsetHeight / window.innerHeight,

            text: spiff.innerHTML,

            fontSize: styleMap.get('font-size').value,
            fontWeight: styleMap.get('font-weight'),
            fontStyle: styleMap.get('font-style'),
            fontFamily: styleMap.get('font-family') || 'sans-serif'

        };
        regions.push(region)
    }

    return regions;
}


/**
 * 
 * @param {HTMLCanvasElement} canvas 
 * @param {*} context
 * @param {*} regions 
 */
export function RenderSpiffs(canvas, ctx, regions, texts, resolutionFactor){
    
    ctx.reset();
    for (let i = 0 ; i < regions.length; i ++){
        let region = regions[i];

        ctx.beginPath(); // Start a new path
      
        ctx.fillStyle = "white";

        // https://developer.mozilla.org/en-US/docs/Web/API/CanvasRenderingContext2D/roundRect
        ctx.roundRect(
            scaleXNormalized(region.xNormalized), scaleYNormalized(region.yNormalized), 
            scaleXNormalized(region.widthNormalized), scaleYNormalized(region.heightNormalized),

            [region.borderTopLeft, region.borderTopRight, region.borderLowRight, region.borderLowLeft]
        );
        ctx.fill();
    }

    for (let i = 0 ; i < texts.length; i ++){
        let text = texts[i];

        ctx.textAlign = 'center';
        let x = scaleXNormalized(text.xNormalized);
        let w = scaleXNormalized(text.widthNormalized);
        let centerX = (x + w*(resolutionFactor));
        let centerY = scaleYNormalized(text.yNormalized) + scaleYNormalized(text.heightNormalized) * .75;

        let fontStr = `${text.fontWeight} ${text.fontSize*.5}px ${text.fontFamily}`;
        // console.log(fontStr)
        ctx.font = fontStr;
        ctx.fillText(text.text, centerX, centerY)
        
    }
   

    function scaleXNormalized(x){
        return x * canvas.width;
    }
    function scaleYNormalized(y){
        return y * canvas.height;
    }
}