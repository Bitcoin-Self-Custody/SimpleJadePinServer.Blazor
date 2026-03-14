// JS interop bridge for html5-qrcode camera scanning.
// Only responsibility: capture raw QR strings from the camera and forward them
// to C# (Blazor) via DotNetObjectReference.invokeMethodAsync.
//
// API:
//   QrInterop.startScanning(elementId, dotNetRef) — attach scanner to the given DOM element
//   QrInterop.stopScanning()                      — tear down and clean up

let scanner = null;

window.QrInterop = {
    startScanning: function (elementId, dotNetRef) {
        scanner = new Html5QrcodeScanner(elementId, {
            fps: 10,
            // Only decode QR codes — ignore barcodes and other formats
            formatsToSupport: [Html5QrcodeSupportedFormats.QR_CODE],
            verbose: false
        });
        scanner.render(
            // Success callback: pass the raw QR string up to C#
            function (result) {
                dotNetRef.invokeMethodAsync('OnQrScanned', result);
            },
            // Error callback: silently ignore per-frame decode failures (normal for camera scanning)
            function (err) { }
        );
    },

    stopScanning: function () {
        if (scanner) {
            scanner.clear();
            scanner = null;
        }
    }
};
