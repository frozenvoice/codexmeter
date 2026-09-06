function companionPopupView(state) {
  var connected = !!(state && state.connected);
  return {
    connected: connected,
    status: connected ? "Connected to ProMeter" : "Disconnected",
    error: connected ? "" : String((state && state.error) || ""),
    button: connected ? "Reconnect to ProMeter" : "Connect to ProMeter"
  };
}

if (typeof module === "object" && module.exports) {
  module.exports = { companionPopupView: companionPopupView };
}

if (typeof document !== "undefined" && document.addEventListener) {
  document.addEventListener("DOMContentLoaded", function () {
    var status = document.getElementById("status");
    var error = document.getElementById("error");
    var button = document.getElementById("connect");

    function render(state) {
      var view = companionPopupView(state);
      status.textContent = view.status;
      error.textContent = view.error;
      button.textContent = view.button;
    }

    chrome.runtime.sendMessage({ type: "status" }, render);
    chrome.storage.onChanged.addListener(function (changes) {
      if (changes.companionConnected || changes.companionLastError) {
        chrome.runtime.sendMessage({ type: "status" }, render);
      }
    });

    button.addEventListener("click", function () {
      chrome.runtime.sendMessage({ type: "connect" }, function (response) {
        render(response);
        if (!(response && response.connected)) {
          status.textContent = "Connecting. Keep this browser signed in to ChatGPT.";
        }
      });
    });
  });
}
