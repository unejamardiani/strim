// Applies the stored theme (#580) before the stylesheets paint. A classic script without defer, so it runs while
// <head> is parsed; a module would run too late and the page would flash in the system theme.
(() => {
  const key = "strim.theme";
  const root = document.documentElement;
  const forced = (theme) => theme === "light" || theme === "dark";

  const show = (theme) => {
    if (forced(theme)) root.dataset.theme = theme;
    else delete root.dataset.theme;
  };

  try {
    show(localStorage.getItem(key));
  } catch {
    // Storage blocked: the system decides.
  }

  window.strimTheme = {
    // "light" or "dark" forces that theme; anything else returns to the system setting.
    apply(theme) {
      show(theme);
      try {
        if (forced(theme)) localStorage.setItem(key, theme);
        else localStorage.removeItem(key);
      } catch {
        // Storage blocked: the choice lasts until the page reloads.
      }
    },
  };
})();
