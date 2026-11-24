# Webhook traffic includes signed headers; disable ngrok's request inspector.
$ErrorActionPreference = 'Stop'
ngrok http http://127.0.0.1:5081 --inspect=false --log=stdout --log-format=json
if ($LASTEXITCODE -ne 0) { throw 'The local webhook tunnel stopped. Check ngrok account configuration.' }
