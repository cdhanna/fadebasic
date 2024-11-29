<script>
  import svelteLogo from './assets/svelte.svg'
  import viteLogo from '/vite.svg'
  import SpiffyBackdrop from './lib/SpiffyBackdrop.svelte'
</script>

<main>
  <SpiffyBackdrop/>

  <div>
    <span class="spiff-text" style="font-size:200px">FADE</span>
  </div>
  <div>
    <span class="spiff-text" style="font-size:30px; font-family: impact">Fade's Actually Dotnet Embeddable</span>
  </div>
  <div class="spiff">
    <span>hello world</span>
  </div>
  <div>
    <div class="spiff">
      <span>Content and such</span>
    </div>
  </div>
</main>

<style>
  :global(body) {
    overscroll-behavior-y: none;
  }
  .spiff-text {
    font-size: 120px;
    font-weight: bold;
    font-family: cursive;
  }
  .spiff {
    color: black;
    border: solid 2px black;

    /* border-radius: 100px; */
    border-top-right-radius: 40px;
    border-top-left-radius: 40px;
    border-bottom-left-radius: 20px;
    border-bottom-right-radius: 20px;

    /* background-color: rgba(0, 0, 0, .5); */
    background-color: white;
    margin: 40px;
    padding: 12px;
    position: relative;
    /* opacity: .0; */
  }
  .spiff.left {
    left: 100px;
  }
  .spiff.right {
    right: 100px;
  }
  
</style>
