using FadeBasic;
using FadeBasic.SourceGenerators;
using FadeBasic.Virtual;

namespace Tests;

// AUTO-GENERATED no-op stubs of Fade.MonoGame.Lib host commands so killcode runs
// headless in the core VM. Input commands are driven to PULSE 'pressed' so the
// game walks its menus and hits notes (exercising gameplay allocation paths).
public partial class MonoStubs
{
    private static int _id = 1;
    private static int _tick;
    [FadeBasicCommand("push asset", FadeBasicCommandUsage.Macro)]
    public static void Push(string path) {  }
    [FadeBasicCommand("rename asset", FadeBasicCommandUsage.Macro)]
    public static void RenameCurrent(string name) {  }
    [FadeBasicCommand("texture compression", FadeBasicCommandUsage.Macro)]
    public static void SetTextureCompression(string assetName, string format) {  }
    [FadeBasicCommand("default texture compression", FadeBasicCommandUsage.Macro)]
    public static void DefaultTextureCompression(string format) {  }
    [FadeBasicCommand("sound compression", FadeBasicCommandUsage.Macro)]
    public static void SetSoundCompression(string assetName, string format) {  }
    [FadeBasicCommand("default sound compression", FadeBasicCommandUsage.Macro)]
    public static void DefaultSoundCompression(string format) {  }
    [FadeBasicCommand("font size", FadeBasicCommandUsage.Macro)]
    public static void SetFontSize(string assetName, int sizePx) {  }
    [FadeBasicCommand("free sfx clip id")]
    public static int GetFreeSfxClipNextId(ref int sfxClipId) { sfxClipId = _id++; return _id++; }
    [FadeBasicCommand("reserve sfx clip id")]
    public static int ReserveSfxClipNextId(ref int sfxClipId) { sfxClipId = _id++; return _id++; }
    [FadeBasicCommand("free sfx id")]
    public static int GetFreeSfxNextId(ref int sfxId) { sfxId = _id++; return _id++; }
    [FadeBasicCommand("reserve sfx id")]
    public static int ReserveSfxNextId(ref int sfxId) { sfxId = _id++; return _id++; }
    [FadeBasicCommand("load sfx clip")]
    public static void LoadSoundEffect(int clipId, string path) {  }
    [FadeBasicCommand("sfx")]
    public static void CreateSoundEffect(int sfxId, int clipId) {  }
    [FadeBasicCommand("pause sfx")]
    public static void PauseSfx(int sfxId) {  }
    [FadeBasicCommand("resume sfx")]
    public static void ResumeSfx(int sfxId) {  }
    [FadeBasicCommand("play sfx")]
    public static void PlaySfx(int sfxId) {  }
    [FadeBasicCommand("delay play sfx")]
    public static void PlaySfx(int sfxId, int delayMs) {  }
    [FadeBasicCommand("set sfx pitch")]
    public static void SetSfxPitch(int sfxId, float pitch) {  }
    [FadeBasicCommand("sfx pitch")]
    public static float GetSfxPitch(int sfxId) { return 0f; }
    [FadeBasicCommand("set sfx pan")]
    public static void SetSfxPan(int sfxId, float pan) {  }
    [FadeBasicCommand("sfx pan")]
    public static float GetSfxPan(int sfxId) { return 0f; }
    [FadeBasicCommand("set sfx volume")]
    public static void SetSfxVolume(int sfxId, float volume) {  }
    [FadeBasicCommand("sfx volume")]
    public static float GetSfxVolume(int sfxId) { return 0f; }
    [FadeBasicCommand("set sfx loop")]
    public static void SetSfxLoop(int sfxId, bool isLooped) {  }
    [FadeBasicCommand("is sfx done")]
    public static bool IsSfxDone(int sfxId) { return false; }
    [FadeBasicCommand("box collider")]
    public static void CreateBoxCollider(int colliderId, int x, int y, int w, int h) {  }
    [FadeBasicCommand("attach collider to transform")]
    public static void AttachColliderToTransform(int colliderId, int transformId) {  }
    [FadeBasicCommand("perform collider checks")]
    public static void DoHitCheck() {  }
    [FadeBasicCommand("get collision")]
    public static bool AreCollidersHitting(int aColliderId, int bColliderId) { return false; }
    [FadeBasicCommand("snap collider to sprite")]
    public static void SnapColliderToSprite(int colliderId, int spriteId) {  }
    [FadeBasicCommand("game ms")]
    public static double GameTime() { return 0.0; }
    [FadeBasicCommand("go kaboom")]
    public static void Throw() {  }
    [FadeBasicCommand("begin debug window")]
    public static void Debug_BeginWindow([FromVm] VirtualMachine vm, string name) {  }
    [FadeBasicCommand("end debug window")]
    public static void Debug_EndWindow([FromVm] VirtualMachine vm) {  }
    [FadeBasicCommand("debug same line")]
    public static void Debug_SameLine([FromVm] VirtualMachine vm) {  }
    [FadeBasicCommand("debug separator")]
    public static void Debug_Separator([FromVm] VirtualMachine vm) {  }
    [FadeBasicCommand("begin debug tree")]
    public static int Debug_BeginTree([FromVm] VirtualMachine vm, string label) { return _id++; }
    [FadeBasicCommand("end debug tree")]
    public static void Debug_EndTree([FromVm] VirtualMachine vm) {  }
    [FadeBasicCommand("begin debug tab bar")]
    public static int Debug_BeginTabBar([FromVm] VirtualMachine vm, string id) { return _id++; }
    [FadeBasicCommand("end debug tab bar")]
    public static void Debug_EndTabBar([FromVm] VirtualMachine vm) {  }
    [FadeBasicCommand("begin debug tab")]
    public static int Debug_BeginTab([FromVm] VirtualMachine vm, string label) { return _id++; }
    [FadeBasicCommand("end debug tab")]
    public static void Debug_EndTab([FromVm] VirtualMachine vm) {  }
    [FadeBasicCommand("debug label")]
    public static void Debug_Label([FromVm] VirtualMachine vm, string label, string value) {  }
    [FadeBasicCommand("debug text")]
    public static void Debug_Text([FromVm] VirtualMachine vm, string text) {  }
    [FadeBasicCommand("debug button")]
    public static int Debug_Button([FromVm] VirtualMachine vm, string name) { return _id++; }
    [FadeBasicCommand("debug toggle")]
    public static int Debug_Toggle([FromVm] VirtualMachine vm, string label, ref int value) { value = _id++; return _id++; }
    [FadeBasicCommand("debug textbox")]
    public static int Debug_TextBox([FromVm] VirtualMachine vm, string label, ref string value, string placeholder = "", int maxLength = 512) { value = ""; return _id++; }
    [FadeBasicCommand("debug int slider")]
    public static int Debug_IntSlider([FromVm] VirtualMachine vm, string name, ref int value, int min = 0, int max = 100) { value = _id++; return _id++; }
    [FadeBasicCommand("debug float slider")]
    public static int Debug_FloatSlider([FromVm] VirtualMachine vm, string label, ref float value, float min = 0, float max = 100) { value = 0f; return _id++; }
    [FadeBasicCommand("debug drag int")]
    public static int Debug_DragInt([FromVm] VirtualMachine vm, string label, ref int value) { value = _id++; return _id++; }
    [FadeBasicCommand("debug drag float")]
    public static int Debug_DragFloat([FromVm] VirtualMachine vm, string label, ref float value) { value = 0f; return _id++; }
    [FadeBasicCommand("debug color picker")]
    public static int Debug_ColorPicker([FromVm] VirtualMachine vm, string label, ref int colorCode) { colorCode = _id++; return _id++; }
    [FadeBasicCommand("enable debug inspector")]
    public static void Debug_EnableInspector() {  }
    [FadeBasicCommand("disable debug inspector")]
    public static void Debug_DisableInspector() {  }
    [FadeBasicCommand("debug browse sprites")]
    public static void Debug_BrowseSprites([FromVm] VirtualMachine vm) {  }
    [FadeBasicCommand("debug browse effects")]
    public static void Debug_BrowseEffects([FromVm] VirtualMachine vm) {  }
    [FadeBasicCommand("debug browse transforms")]
    public static void Debug_BrowseTransforms([FromVm] VirtualMachine vm) {  }
    [FadeBasicCommand("debug browse tweens")]
    public static void Debug_BrowseTweens([FromVm] VirtualMachine vm) {  }
    [FadeBasicCommand("debug browse colliders")]
    public static void Debug_BrowseColliders([FromVm] VirtualMachine vm) {  }
    [FadeBasicCommand("debug browse texts")]
    public static void Debug_BrowseTexts([FromVm] VirtualMachine vm) {  }
    [FadeBasicCommand("debug browse sfx")]
    public static void Debug_BrowseSfx([FromVm] VirtualMachine vm) {  }
    [FadeBasicCommand("debug browse textures")]
    public static void Debug_BrowseTextures([FromVm] VirtualMachine vm) {  }
    [FadeBasicCommand("debug browse render outputs")]
    public static void Debug_BrowseRenderOutputs([FromVm] VirtualMachine vm) {  }
    [FadeBasicCommand("debug console")]
    public static void Debug_Console([FromVm] VirtualMachine vm) {  }
    [FadeBasicCommand("debug inspector")]
    public static void Debug_Inspector([FromVm] VirtualMachine vm) {  }
    [FadeBasicCommand("debug metadata")]
    public static void Debug_Metadata([FromVm] VirtualMachine vm) {  }
    [FadeBasicCommand("debug sprite")]
    public static int Debug_Sprite([FromVm] VirtualMachine vm, int spriteId) { return _id++; }
    [FadeBasicCommand("debug effect")]
    public static int Debug_Effect([FromVm] VirtualMachine vm, int effectId) { return _id++; }
    [FadeBasicCommand("debug transform")]
    public static int Debug_Transform([FromVm] VirtualMachine vm, int transformId) { return _id++; }
    [FadeBasicCommand("debug tween")]
    public static int Debug_Tween([FromVm] VirtualMachine vm, int tweenId) { return _id++; }
    [FadeBasicCommand("debug collider")]
    public static int Debug_Collider([FromVm] VirtualMachine vm, int colliderId) { return _id++; }
    [FadeBasicCommand("debug text sprite")]
    public static int Debug_TextSprite([FromVm] VirtualMachine vm, int textId) { return _id++; }
    [FadeBasicCommand("debug sfx")]
    public static int Debug_Sfx([FromVm] VirtualMachine vm, int sfxId) { return _id++; }
    [FadeBasicCommand("debug texture")]
    public static int Debug_Texture([FromVm] VirtualMachine vm, int textureId) { return _id++; }
    [FadeBasicCommand("debug render output")]
    public static int Debug_RenderOutput([FromVm] VirtualMachine vm, int outputId) { return _id++; }
    [FadeBasicCommand("enable gizmos")]
    public static void EnableGizmos() {  }
    [FadeBasicCommand("disable gizmos")]
    public static void DisableGizmos() {  }
    [FadeBasicCommand("enable sprite gizmo")]
    public static void EnableSpriteGizmo(int spriteId, int thickness=0, int colorCode=0) {  }
    [FadeBasicCommand("disable sprite gizmo")]
    public static void DisableSpriteGizmo(int spriteId) {  }
    [FadeBasicCommand("get sprite gizmo enabled")]
    public static int GetSpriteGizmoEnabled(int spriteId) { return _id++; }
    [FadeBasicCommand("set sprite gizmo color")]
    public static void SetSpriteGizmoColor(int spriteId, int packedColor) {  }
    [FadeBasicCommand("set sprite gizmo thickness")]
    public static void SetSpriteGizmoThickness(int spriteId, float thickness) {  }
    [FadeBasicCommand("enable collider gizmo")]
    public static void EnableColliderGizmo(int colliderId, int thickness=0, int colorCode=0) {  }
    [FadeBasicCommand("disable collider gizmo")]
    public static void DisableColliderGizmo(int colliderId) {  }
    [FadeBasicCommand("get collider gizmo enabled")]
    public static int GetColliderGizmoEnabled(int colliderId) { return _id++; }
    [FadeBasicCommand("set collider gizmo color")]
    public static void SetColliderGizmoColor(int colliderId, int packedColor) {  }
    [FadeBasicCommand("set collider gizmo thickness")]
    public static void SetColliderGizmoThickness(int colliderId, float thickness) {  }
    [FadeBasicCommand("enable text gizmo")]
    public static void EnableTextGizmo(int textId, int thickness=0, int colorCode=0) {  }
    [FadeBasicCommand("disable text gizmo")]
    public static void DisableTextGizmo(int textId) {  }
    [FadeBasicCommand("get text gizmo enabled")]
    public static int GetTextGizmoEnabled(int textId) { return _id++; }
    [FadeBasicCommand("set text gizmo color")]
    public static void SetTextGizmoColor(int textId, int packedColor) {  }
    [FadeBasicCommand("set text gizmo thickness")]
    public static void SetTextGizmoThickness(int textId, float thickness) {  }
    [FadeBasicCommand("gizmo line")]
    public static void GizmoLine(float x1, float y1, float x2, float y2, int packedColor = -1, float thickness = 1f) {  }
    [FadeBasicCommand("gizmo rect")]
    public static void GizmoRect(float x, float y, float w, float h, int packedColor = -1, float thickness = 1f) {  }
    [FadeBasicCommand("mouse x")]
    public static int GetMouseX() { return _id++; }
    [FadeBasicCommand("mouse y")]
    public static int GetMouseY() { return _id++; }
    [FadeBasicCommand("left click")]
    public static bool IsLeftMouse() { return (_tick % 41) == 0; }
    [FadeBasicCommand("new left click")]
    public static bool IsNewLeftMouse() { return (_tick % 37) == 0; }
    [FadeBasicCommand("new right click")]
    public static bool IsNewRightMouse() { return false; }
    [FadeBasicCommand("right click")]
    public static bool IsRightMouse() { return false; }
    [FadeBasicCommand("rightKey")]
    public static int rightKey() { return _id++; }
    [FadeBasicCommand("leftKey")]
    public static int leftKey() { return _id++; }
    [FadeBasicCommand("spaceKey")]
    public static int spaceKey() { return _id++; }
    [FadeBasicCommand("left shiftKey")]
    public static int shiftKey() { return _id++; }
    [FadeBasicCommand("new left shiftKey")]
    public static int shiftKeyNew() { return _id++; }
    [FadeBasicCommand("new upkey")]
    public static bool upKeyNew() { return (_tick % 31) == 0; }
    [FadeBasicCommand("new downkey")]
    public static bool downKeyNew() { return (_tick % 29) == 0; }
    [FadeBasicCommand("new rightKey")]
    public static bool rightKeyNew() { return false; }
    [FadeBasicCommand("new leftKey")]
    public static bool leftKeyNew() { return false; }
    [FadeBasicCommand("new spaceKey")]
    public static bool spaceKeyNew() { return false; }
    [FadeBasicCommand("new key down")]
    public static bool IsNewKeyPressed(int scanCode) { _tick++; return (_tick % 17) == 0; }
    [FadeBasicCommand("key down")]
    public static bool IsKeyPressed(int scanCode) { return (_tick % 23) == 0; }
    [FadeBasicCommand("scanCode")]
    public static int ScanCode(string key) { return _id++; }
    [FadeBasicCommand("mouse over sprite")]
    public static bool IsMouseOverSprite(int spriteId) { return false; }
    [FadeBasicCommand("point over sprite")]
    public static bool IsPointOverSprite(int spriteId, float x, float y) { return false; }
    [FadeBasicCommand("mouse over collider")]
    public static bool IsMouseOverCollider(int colliderId) { return false; }
    [FadeBasicCommand("point over collider")]
    public static bool IsPointOverCollider(int colliderId, float x, float y) { return false; }
    [FadeBasicCommand("sin")]
    public static float Sin(float x) { return 0f; }
    [FadeBasicCommand("cos")]
    public static float Cos(float x) { return 0f; }
    [FadeBasicCommand("atan2")]
    public static float Atan2(float y, float x) { return 0f; }
    [FadeBasicCommand("atan")]
    public static float Atan(float x) { return 0f; }
    [FadeBasicCommand("sqrt")]
    public static float Sqrt(float x) { return 0f; }
    [FadeBasicCommand("abs")]
    public static float Abs(float x) { return 0f; }
    [FadeBasicCommand("sign")]
    public static float Sign(float x) { return 0f; }
    [FadeBasicCommand("max")]
    public static float Max(float a, float b) { return 0f; }
    [FadeBasicCommand("min")]
    public static float Min(float a, float b) { return 0f; }
    [FadeBasicCommand("pow")]
    public static float Pow(float a, float b) { return 0f; }
    [FadeBasicCommand("log")]
    public static float Log(float a, float b) { return 0f; }
    [FadeBasicCommand("deg")]
    public static float Deg(float radians) { return 0f; }
    [FadeBasicCommand("rad")]
    public static float Rad(float degrees) { return 0f; }
    [FadeBasicCommand("screenshot")]
    public static void TakeSnapshot(string filePath) {  }
    [FadeBasicCommand("set render size")]
    public static void SetRenderSize(int width, int height) {  }
    [FadeBasicCommand("render width")]
    public static int GetRenderWidth() { return _id++; }
    [FadeBasicCommand("render height")]
    public static int GetRenderHeight() { return _id++; }
    [FadeBasicCommand("set background color")]
    public static void SetBackgroundColor(int colorCode) {  }
    [FadeBasicCommand("free effect id")]
    public static int GetFreeEffectNextId(ref int effectId) { effectId = _id++; return _id++; }
    [FadeBasicCommand("reserve effect id")]
    public static int ReserveEffectNextId(ref int effectId) { effectId = _id++; return _id++; }
    [FadeBasicCommand("effect")]
    public static void LoadEffect(int effectId, string effectName) {  }
    [FadeBasicCommand("set screen shake amount")]
    public static void SetScreenShakeMag(float mag) {  }
    [FadeBasicCommand("set screen shake bounce")]
    public static void SetScreenShakeBounce(float bounce) {  }
    [FadeBasicCommand("set effect param color")]
    public static void SetEffectParameter_ColorInt(int effectId, string parameterName, int colorCode) {  }
    [FadeBasicCommand("set effect param float")]
    public static void SetEffectParameter_Float(int effectId, string parameterName, float value) {  }
    [FadeBasicCommand("set effect param float2")]
    public static void SetEffectParameter_Float2(int effectId, string parameterName, float x, float y) {  }
    [FadeBasicCommand("set effect param float3")]
    public static void SetEffectParameter_Float3(int effectId, string parameterName, float x, float y, float z) {  }
    [FadeBasicCommand("set effect param float4")]
    public static void SetEffectParameter_Float4(int effectId, string parameterName, float x, float y, float z, float w) {  }
    [FadeBasicCommand("set effect param texture")]
    public static void SetEffectParameter_Texture(int effectId, string parameterName, int textureId) {  }
    [FadeBasicCommand("clear screen effect")]
    public static void ClearScreenEffect() {  }
    [FadeBasicCommand("set screen effect")]
    public static void SetScreenEffect(int effectId) {  }
    [FadeBasicCommand("set stage sampler")]
    public static void SetSamplerState(int stageId, int mode) {  }
    [FadeBasicCommand("set render target background color")]
    public static void SetRenderTargetBackground(int outputId, int colorCode) {  }
    [FadeBasicCommand("set render target clear flags")]
    public static void SetRenderTargetClearFlags(int outputId, int clearTarget) {  }
    [FadeBasicCommand("render target texture")]
    public static int GetRenderTargetTexture(int outputId) { return _id++; }
    [FadeBasicCommand("free render target id")]
    public static int GetFreeOutputNextId(ref int outputId) { outputId = _id++; return _id++; }
    [FadeBasicCommand("reserve render target id")]
    public static int ReserveOutputNextId(ref int outputId) { outputId = _id++; return _id++; }
    [FadeBasicCommand("render target")]
    public static void SetRenderTargetTexture(int outputId, int textureId=0) {  }
    [FadeBasicCommand("set fullscreen")]
    public static void SetFullScreen(bool fullScreen) {  }
    [FadeBasicCommand("set window title")]
    public static void SetWindowTitle(string title) {  }
    [FadeBasicCommand("is os windows")]
    public static int IsWindows() { return _id++; }
    [FadeBasicCommand("is os mac")]
    public static int IsMac() { return _id++; }
    [FadeBasicCommand("display width")]
    public static int DisplayWidth() { return _id++; }
    [FadeBasicCommand("display height")]
    public static int DisplayHeight() { return _id++; }
    [FadeBasicCommand("screen width")]
    public static int ScreenWidth() { return _id++; }
    [FadeBasicCommand("screen height")]
    public static int ScreenHeight() { return _id++; }
    [FadeBasicCommand("set screen size")]
    public static void SetScreenResolution(int width, int height) {  }
    [FadeBasicCommand("free sprite id")]
    public static int GetFreeSpriteNextId(ref int spriteId) { spriteId = _id++; return _id++; }
    [FadeBasicCommand("reserve sprite id")]
    public static int ReserveSpriteNextId(ref int spriteId) { spriteId = _id++; return _id++; }
    [FadeBasicCommand("sprite")]
    public static void Sprite(int spriteId, float x, float y, int textureId) {  }
    [FadeBasicCommand("position sprite")]
    public static void PositionSprite(int spriteId, float x, float y) {  }
    [FadeBasicCommand("color sprite")]
    public static void ColorSprite(int spriteId, int packedColor) {  }
    [FadeBasicCommand("order sprite")]
    public static void OrderSprite(int spriteId, int order) {  }
    [FadeBasicCommand("hide sprite")]
    public static void HideSprite(int spriteId) {  }
    [FadeBasicCommand("show sprite")]
    public static void ShowSprite(int spriteId) {  }
    [FadeBasicCommand("set sprite texture")]
    public static void SetSpriteTexture(int spriteId, int textureId) {  }
    [FadeBasicCommand("set sprite render target")]
    public static void SetSpriteTarget(int spriteId, int outputId) {  }
    [FadeBasicCommand("reset sprite render target")]
    public static void ResetSpriteTarget(int spriteId) {  }
    [FadeBasicCommand("add sprite render target")]
    public static void AddSpriteTarget(int spriteId, int outputId) {  }
    [FadeBasicCommand("scale sprite")]
    public static void ScaleSprite(int spriteId, float x, float y) {  }
    [FadeBasicCommand("attach sprite to transform")]
    public static void SetSpriteRelativeToAnother(int spriteId, int transformId) {  }
    [FadeBasicCommand("size sprite")]
    public static void SizeSprite(int spriteId, float xPixels, float yPixels) {  }
    [FadeBasicCommand("size sprite x")]
    public static void SizeSpriteAspectX(int spriteId, float xPixels) {  }
    [FadeBasicCommand("size sprite y")]
    public static void SizeSpriteAspectY(int spriteId, float yPixels) {  }
    [FadeBasicCommand("rotate sprite")]
    public static void RotateSprite(int spriteId, float angle) {  }
    [FadeBasicCommand("set sprite offset")]
    public static void SetSpriteOffset(int spriteId, float xRatio, float yRatio) {  }
    [FadeBasicCommand("set sprite all texcoord1")]
    public static void SetSpriteTexcoord1(int spriteId, float x, float y, float z, float w) {  }
    [FadeBasicCommand("set sprite index texcoord1")]
    public static void SetSpriteTexcoord1(int spriteId, int cornerIndex, float x, float y, float z, float w) {  }
    [FadeBasicCommand("set sprite effect")]
    public static void SetSpriteEffect(int spriteId, int effectId) {  }
    [FadeBasicCommand("set sprite diffuse")]
    public static void SetSpriteDiffuse(int spriteId, byte red, byte green, byte blue) {  }
    [FadeBasicCommand("set sprite alpha")]
    public static void SetSpriteDiffuse(int spriteId, byte alpha) {  }
    [FadeBasicCommand("set sprite frame")]
    public static void SetSpriteFrame(int spriteId, int frameId) {  }
    [FadeBasicCommand("set sprite flip")]
    public static void Flip(int spriteId, int flipHorizontal, int flipVertical) {  }
    [FadeBasicCommand("sprite width")]
    public static float GetSpriteWidth(int spriteId) { return 0f; }
    [FadeBasicCommand("sprite height")]
    public static float GetSpriteHeight(int spriteId) { return 0f; }
    [FadeBasicCommand("sprite x")]
    public static float SpriteX(int spriteId) { return 0f; }
    [FadeBasicCommand("sprite y")]
    public static float SpriteY(int spriteId) { return 0f; }
    [FadeBasicCommand("set sync rate")]
    public static void SetSyncRate(int rate) {  }
    [FadeBasicCommand("frame number")]
    public static long Sync() { return _id++; }
    [FadeBasicCommand("free text id")]
    public static int GetFreeTextNextId(ref int textId) { textId = _id++; return _id++; }
    [FadeBasicCommand("reserve text id")]
    public static int ReserveTextNextId(ref int textId) { textId = _id++; return _id++; }
    [FadeBasicCommand("text")]
    public static void Text(int textId, int x, int y, int spriteFontId, string text) {  }
    [FadeBasicCommand("set text")]
    public static void SetText(int textId, string text) {  }
    [FadeBasicCommand("set text position")]
    public static void SetTextPosition(int textId, int x, int y) {  }
    [FadeBasicCommand("color text")]
    public static void SetTextColor(int textId, int colorCode) {  }
    [FadeBasicCommand("color text drop shadow")]
    public static void SetTextDropShadowColor(int textId, int colorCode) {  }
    [FadeBasicCommand("enable text drop shadow")]
    public static void EnableTextDropShadow(int textId, int x, int y, int colorCode) {  }
    [FadeBasicCommand("disable text drop shadow")]
    public static void DisableTextDropShadow(int textId) {  }
    [FadeBasicCommand("set text alpha")]
    public static void SetTextDiffuse(int textId, byte alpha) {  }
    [FadeBasicCommand("scale text")]
    public static void SetTextScale(int textId, float x, float y) {  }
    [FadeBasicCommand("order text")]
    public static void SetTextOrder(int textId, int order) {  }
    [FadeBasicCommand("hide text")]
    public static void HideSpriteText(int textId) {  }
    [FadeBasicCommand("show text")]
    public static void ShowpriteText(int textId) {  }
    [FadeBasicCommand("set text render target")]
    public static void SetSpriteTextRenderTarget(int textId, int outputId) {  }
    [FadeBasicCommand("reset text render target")]
    public static void ResetSpriteTextRenderTarget(int textId) {  }
    [FadeBasicCommand("add text render target")]
    public static void AddSpriteTextRenderTarget(int textId, int outputId) {  }
    [FadeBasicCommand("size text")]
    public static void SizeText(int textId, float xPixels, float yPixels) {  }
    [FadeBasicCommand("size text x")]
    public static void SizeSpriteTextAspectX(int textId, float xPixels) {  }
    [FadeBasicCommand("size text x")]
    public static void SizeSpriteTextAspectX(int textId, float xPixels, float min, float max) {  }
    [FadeBasicCommand("size text y")]
    public static void SizeSpriteTextAspectY(int textId, float yPixels) {  }
    [FadeBasicCommand("get text size x")]
    public static float GetTextSizeX(int textId) { return 0f; }
    [FadeBasicCommand("get text size y")]
    public static float GetTextSizeY(int textId) { return 0f; }
    [FadeBasicCommand("attach text to transform")]
    public static void SetSpriteTextRelativeToAnother(int textId, int transformId) {  }
    [FadeBasicCommand("rotate text")]
    public static void RotateSpriteText(int textId, float angle) {  }
    [FadeBasicCommand("set text offset")]
    public static void SetSpriteTextOffset(int textId, float xRatio, float yRatio) {  }
    [FadeBasicCommand("text x")]
    public static float TextX(int textId) { return 0f; }
    [FadeBasicCommand("text y")]
    public static float TextY(int textId) { return 0f; }
    [FadeBasicCommand("font")]
    public static void LoadSpriteFont(int fontId, string filePath) {  }
    [FadeBasicCommand("free texture id")]
    public static int GetFreeTextureNextId(ref int textureId) { textureId = _id++; return _id++; }
    [FadeBasicCommand("reserve texture id")]
    public static int ReserveTextureNextId(ref int textureId) { textureId = _id++; return _id++; }
    [FadeBasicCommand("texture")]
    public static void LoadTexture(int textureId, string filePath) {  }
    [FadeBasicCommand("set texture frame grid")]
    public static void SetTextureFramesByRowCol(int textureId, int rows, int columns) {  }
    [FadeBasicCommand("texture frames")]
    public static int GetTextureFrameCount(int textureId) { return _id++; }
    [FadeBasicCommand("texture width")]
    public static int GetTextureWidth(int textureId) { return _id++; }
    [FadeBasicCommand("texture height")]
    public static int GetTextureHeight(int textureId) { return _id++; }
    [FadeBasicCommand("texture aspect")]
    public static float GetTextureAspect(int textureId) { return 0f; }
    [FadeBasicCommand("free transform id")]
    public static int GetFreeTransformNextId(ref int transformId) { transformId = _id++; return _id++; }
    [FadeBasicCommand("reserve transform id")]
    public static int ReserveTransformNextId(ref int transformId) { transformId = _id++; return _id++; }
    [FadeBasicCommand("transform")]
    public static void CreateTransform(int transformId, float x, float y) {  }
    [FadeBasicCommand("set transform position")]
    public static void SetTransformPosition(int transformId, float x, float y) {  }
    [FadeBasicCommand("get local transform x")]
    public static float GetTransformLocalX(int transformId) { return 0f; }
    [FadeBasicCommand("get local transform y")]
    public static float GetTransformLocalY(int transformId) { return 0f; }
    [FadeBasicCommand("get local transform scale x")]
    public static float GetTransformLocalScaleX(int transformId) { return 0f; }
    [FadeBasicCommand("get local transform scale y")]
    public static float GetTransformLocalScaleY(int transformId) { return 0f; }
    [FadeBasicCommand("set transform scale")]
    public static void SetTransformScale(int transformId, float x, float y) {  }
    [FadeBasicCommand("set transform rotation")]
    public static void SetTransformRotation(int transformId, float angle) {  }
    [FadeBasicCommand("set transform parent")]
    public static void SetTransformParent(int transformId, int parentTransformId) {  }
    [FadeBasicCommand("free tween id")]
    public static int GetFreeTweenNextId(ref int tweenId) { tweenId = _id++; return _id++; }
    [FadeBasicCommand("reserve tween id")]
    public static int ReserveTweenNextId(ref int tweenId) { tweenId = _id++; return _id++; }
    [FadeBasicCommand("create basic tween")]
    public static void CreateTween(int tweenId, float start, float end, float duration, float delay) {  }
    [FadeBasicCommand("set tween easing")]
    public static void SetTweenEasing(int tweenId, int easingType) {  }
    [FadeBasicCommand("set tween type")]
    public static void SetTweenType(int tweenId, int type) {  }
    [FadeBasicCommand("play tween")]
    public static void PlayTween(int tweenId) {  }
    [FadeBasicCommand("tweenVal")]
    public static float GetTweenValue(int tweenId) { return 0f; }
    [FadeBasicCommand("tweenRatio")]
    public static float GetTweenRatio(int tweenId) { return 0f; }
    [FadeBasicCommand("is tween done")]
    public static bool GetTweenPlaying(int tweenId) { return false; }
    [FadeBasicCommand("any tweens running")]
    public static bool GetAnyTweenPlaying(params int[] tweenIds) { return false; }
}
