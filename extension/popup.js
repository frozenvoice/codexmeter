document.addEventListener("DOMContentLoaded", function () {
  var status = document.getElementById("status");
  var button = document.getElementById("connect");

  function render(connected) {
    status.textContent = connected ? "Connected to ProMeter" : "Disconnected";
  }

  chrome.runtime.sendMessage({ type: "status" }, function (response) {
    render(response && response.connected);
  });

  button.addEventListener("click", function () {
    chrome.runtime.sendMessage({ type: "connect" }, function (response) {
      render(response && response.connected);
      status.textContent = (response && response.connected ? "Connected" : "Connecting") + ". Keep this browser signed in to ChatGPT.";
    });
  });
});
