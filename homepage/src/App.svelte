<script>
  import fadeLogo from "./../../images/ghost_lee.png";
  import fadeScreen from "./../../images/fade_screenshot.png";
</script>

<main>
  <div class="content">
    <img
      src={fadeLogo}
      class="logo"
      alt="Fade Basic Logo"
      title="Ghost Lee"
    />

    <div>
      <h1 style="
        margin-bottom: 0px;
      ">
        FADE BASIC
      </h1>
      <p>
        <i><b>F</b>ade's <b>A</b>ctually <b>D</b>otnet <b>E</b>mbeddable</i>
      </p>
    </div>
  </div>

  <div>
    <div class="buttons" style="
      display: flex;
      flex-direction: row;
      justify-content: space-between;
    ">

      <a href="https://github.com/cdhanna/fadebasic">
        <button>
          Install
        </button>
      </a>
      <a href="https://github.com/cdhanna/fadebasic/blob/main/FadeBasic/book/FadeBook/Language.md">          
        <button>
          Language
        </button>
      </a>
      <a href="https://discord.gg/yxFAFJurvU" unselectable="off">
        <button>
          Discord
        </button>
      </a>

    </div>

    <div style="
      text-align: left;
    ">
      <p>
        <i>Fade</i> is a <b>BASIC</b>-<i>esque</i> scripting language for Dotnet that, 
      </p>
      <ol>
        <li>Is debuggable inside VSCode,</li>
        <li>Uses no runtime Reflection,</li>
        <li>Has minimal dependencies,</li>
        <li>Is Customizable.</li>
      </ol>
      <p> <i>Fade</i> is open source and created by <a href="https://brewed.ink">Chris Hanna</a> </p>
    </div>

  </div>

</main>


<a href={fadeScreen} target="_blank">
  <img
  src={fadeScreen}
  class="screenshot"
  alt="Screenshot of Fade in VSCode"
  title="It works in Visual Studio Code!"
  />
</a>
<style>

</style>
