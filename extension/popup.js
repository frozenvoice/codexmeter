document.addEventListener("DOMContentLoaded", function () {
  var status = document.getElementById("status");
  var error = document.getElementById("error");
  var button = document.getElementById("connect");

  function render(state) {
    status.textContent = state && state.connected ? "Connected to ProMeter" : "Disconnected";
    error.textContent = state && state.error ? state.error : "";
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
