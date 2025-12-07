# Dama server – client protocol

Messages end with `\n`, format `ID;TYPE;param;key=value;...`.

## Login & heartbeat
- `ID;LOGIN;<nick>` → `ID;LOGIN_OK;player=<playerId>` or `ERROR;INVALID_FORMAT|SERVER_FULL`.
- `ID;PING` → `ID;PONG` (send periodically to keep connection alive).

## Lobby
- `ID;LIST_ROOMS` → `ID;ROOMS_EMPTY` or multiple lines `ID;ROOM;id=<id>;name=<name>;players=<count>;status=<WAITING|IN_GAME|FINISHED>`.
- `ID;CREATE_ROOM;<name>` → `ID;CREATE_ROOM_OK;room=<roomId>` or `ERROR;INVALID_FORMAT|SERVER_FULL`.
- `ID;JOIN_ROOM;<roomId>` → `ID;JOIN_ROOM_OK;room=<roomId>;players=<n>/<2>` or `ERROR;ROOM_NOT_FOUND|NOT_LOGGED_IN|ROOM_FULL`.

## Game start
- When room fills: each player gets `ID;GAME_START;room=<roomId>;you=<WHITE|BLACK>`.
- Immediately after: `ID;GAME_STATE;room=<roomId>;turn=<PLAYER1|PLAYER2|NONE>;board=<64 chars>`.

## Moves
- `ID;MOVE;<roomId>;<fromRow>;<fromCol>;<toRow>;<toCol>` → on error `ERROR;...`:
  - general: `INVALID_FORMAT|ROOM_NOT_FOUND|ROOM_NOT_IN_GAME|NOT_LOGGED_IN|NOT_IN_ROOM|NOT_YOUR_TURN|OUT_OF_BOARD|INVALID_SQUARE|NO_PIECE|NOT_YOUR_PIECE|DEST_NOT_EMPTY|INVALID_MOVE|INVALID_DIRECTION`
  - capture/chain: `MUST_CAPTURE|MUST_CONTINUE_CAPTURE|NO_OPPONENT_TO_CAPTURE`
- On success: everyone in room gets new `GAME_STATE`.

## Legal moves helper
- `ID;LEGAL_MOVES;<roomId>;<row>;<col>` → `ID;LEGAL_MOVES;room=<roomId>;from=<row,col>;to=<r1,c1>|<r2,c2>;mustCapture=<0|1>`
- Errors: `INVALID_FORMAT|ROOM_NOT_FOUND|ROOM_NOT_IN_GAME|NOT_LOGGED_IN|NOT_IN_ROOM|NOT_YOUR_PIECE|NO_PIECE|MUST_CONTINUE_CAPTURE`

## Leaving / ending
- `ID;LEAVE_ROOM;<roomId>` → `ID;LEAVE_ROOM_OK;room=<roomId>` or `ERROR;ROOM_NOT_FOUND|NOT_LOGGED_IN|NOT_IN_ROOM`.
- Game ends with `GAME_END;room=<roomId>;reason=<...>;winner=<WHITE|BLACK|NONE>` where reason is one of:
  - `WHITE_WIN_NO_PIECES`, `BLACK_WIN_NO_PIECES`
  - `WHITE_WIN_NO_MOVES`, `BLACK_WIN_NO_MOVES`
  - `OPPONENT_LEFT`, `OPPONENT_TIMEOUT`, `TURN_TIMEOUT`

## Examples
- Login: `1;LOGIN;alice` → `1;LOGIN_OK;player=1`
- Ping: `2;PING` → `2;PONG`
- Create: `3;CREATE_ROOM;MyRoom` → `3;CREATE_ROOM_OK;room=1`
- Join: `4;JOIN_ROOM;1` → `4;JOIN_ROOM_OK;room=1;players=1/2`
- Start: `4;GAME_START;room=1;you=WHITE`, `4;GAME_STATE;room=1;turn=PLAYER1;board=...`
- Move: `5;MOVE;1;5;0;4;1` → if valid: `5;GAME_STATE;room=1;turn=PLAYER2;board=...`
- Leave: `6;LEAVE_ROOM;1` → `6;LEAVE_ROOM_OK;room=1`
- End: `0;GAME_END;room=1;reason=OPPONENT_TIMEOUT`
