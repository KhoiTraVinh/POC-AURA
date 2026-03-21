# SIGNALR CLIENT DEEP DIVE: BÓC TÁCH CƠ CHẾ & HIỆU NĂNG

Tài liệu này giải thích cặn kẽ từng dòng code trong file cấu hình SignalR Client (`chat-signalr.service.ts`), tập trung phân tích sâu về nguyên lý hoạt động nội tại (Under the hood) để bạn có thể tự tin thuyết trình về mức độ tiêu thụ RAM, CPU, Network và tính ổn định của giải pháp.

---

## 1. Cơ chế Retry Vô Tận (Infinite Retry)
```typescript
const infiniteRetry: signalR.IRetryPolicy = {
  nextRetryDelayInMilliseconds(ctx: signalR.RetryContext): number {
    const delays = [0, 2000, 5000, 10000];
    return delays[Math.min(ctx.previousRetryCount, delays.length - 1)];
  }
};

// Bên dưới Builder:
// .withAutomaticReconnect(infiniteRetry) 
```

**Hoạt động thế nào?**
- Mặc định của Microsoft SignalR là `.withAutomaticReconnect()` không có tham số sẽ chỉ thử reconnect lại 4 lần (0s, 2s, 10s, 30s). Nghĩa là sau khoảng 42 giây mà rớt mạng, SignalR sẽ chính thức chốt `Disconnected` và **TỪ BỎ**.
- Khách hàng cài Blazor in bill có thể tháo dây mạng hoặc cúp điện con router tới 1 tiếng. Nếu từ bỏ, app sẽ chết đứng.
- Hàm `infiniteRetry` tự động ép policy trả về mốc thời gian retry cuối cùng (`10000` tức là 10 giây). Điều này khiến SignalR **retry liên tục không bao giờ ngừng** cho đến khi có mạng lại.

**Đánh giá System Resources (CPU/RAM/Network)**:
- **CPU & RAM**: Cực thấp. Hàm này chỉ sử dụng `setTimeout()` của JavaScript/C#. OS sẽ đưa nó vào chế độ ngủ (Sleep) chờ hết thời gian mới gọi lại mạng một lần. Gần như tốn 0% CPU khi rớt mạng. Tốn vài KB RAM lưu trữ Callback.
- **Network**: Bắn một gói tin TCP/IP SYN cực nhỏ (vài chục Bytes) để dò đường mỗi 10 giây. Không hề bị hiện tượng "DDoS nội mạng" do Spam rác.

---

## 2. Loại bỏ Heartbeat (Tối ưu Network/Pin/Băng thông)
```typescript
.withKeepAliveInterval(2_000_000_000)
.withServerTimeout(24 * 60 * 60 * 1000)
```
**Giải thích sâu (Deep Dive)**:
- Theo kiến trúc mặc định, SignalR Client có giao thức "Heartbeat" (Nhịp tim). Phía Client cứ mỗi `15 giây` sẽ âm thầm bắn một gói tin ping (Message `type: 6`) lên Server để nhắc nhở "Tôi còn sống nè". 
- Tuy nhiên, trong mô hình Server bắn Job - Client in tự động, việc mấy trăm con Client liên tục Ping lên sẽ lãng phí Socket và CPU Server xử lý gói rác.
- **Giải pháp**: Thiết lập `KeepAliveInterval` (thời gian Ping) lên một con số siêu to khổng lồ (~23 ngày - `2_000_000_000 ms`). Tham số `ServerTimeout` (Thời gian Client chịu đựng Server im lặng) tăng lên 1 ngày.
- **Kết quả**: Gần như triệt tiêu hoàn toàn Traffic (Ping PONG) rác chạy quẩn quanh mạng LAN/Internet. Trạng thái WebSockets/TCP được duy trì ở mức tĩnh (Idle Connection). Chỉ tốn 1 file descriptor cấp phát tĩnh trên RAM OS (siêu nhẹ).

---

## 3. Tại sao các hàm `onreconnecting`, `onreconnected`, `onclose` tự động chạy? (Góc nhìn OS Kernel & Hardware)
```typescript
this.hubConnection.onreconnecting(() => this.connectionStatusSubject.next('reconnecting'));
this.hubConnection.onreconnected(() => this.connectionStatusSubject.next('connected'));
this.hubConnection.onclose(() => this.connectionStatusSubject.next('disconnected'));
```

Điều gì thực sự xảy ra ở dưới đáy (Physical & OS Layer)?

### Phân tích khi rớt mạng (The Disconnect Event)
1. **Physical Layer (Layer 1) & NIC (Network Interface Card)**: Khi cáp mạng lỏng hoặc Wi-Fi rớt, chip xử lý mạng (NIC Controller) phát hiện mất sóng/mất tín hiệu điện. NIC lập tức phát ra một ngắt phần cứng **(Hardware Interrupt - IRQ)** tới CPU.
2. **CPU & OS Kernel**: CPU nhận IRQ, tạm thời lưu ngữ cảnh thanh ghi hiện tại (Registers Context) vào RAM (`Stack`) và gọi **ISR (Interrupt Service Routine)** của Driver Card mạng. Tại đây, Driver báo cáo "Mất kết nối vật lý".
3. **TCP Stack (OS Level)**: Hệ Điều Hành (VD: Windows/Linux) quản lý bảng trạng thái TCP trong Kernel Memory (RAM). Ngay khi biết rớt mạng vật lý (hoặc thời gian timeout ACK của TCP đã điểm), HĐH chuyển trạng thái của TCP socket đang kết nối tới Server từ `ESTABLISHED` sang `CLOSED`.
4. **The Event Dispatch**: HĐH gửi một tín hiệu (Software Interrupt/Event) tới tiến trình của Blazor/Trình duyệt (Process/Thread). Lúc này, Engine JS/WebAssembly (V8 hoặc .NET CLR) mới tóm được sự kiện `socket.onclose`.

### Cú nhảy từ HĐH lên Code chạy (RAM - Heap & Stack):
- Objects `HubConnection` và mấy cái Closure function `() => this.connectionStatusSubject...` vốn đã được cấp phát nằm nhởn nhơ ở vùng **Heap** (Vùng nhớ cấp phát động) từ lúc khởi tạo ứng dụng. Chúng không chạy liên tục để ngốn CPU.
- Khi sự kiện `socket.onclose` nổ ra, Engine mới bốc địa chỉ của hàm Closure trên Heap, đưa nó vào luồng xử lý chính. 
- CPU lấy bộ nhớ **L1/L2 Cache** (Cache miss có thể xảy ra do HĐH vừa chuyển Thread), nạp chỉ thị mã máy của hàm vào **L1i (Instruction Cache)**. Các tham số/biến cục bộ sẽ được cấp phát nhanh trên vùng **Stack**. Hàm thực thi xong, con trỏ Stack lùi lại, giải phóng bộ nhớ Stack, quá trình ném trạng thái sang `Reconnecting` cực kỳ nhẹ nhàng, không sinh ra Garbage (Rác sinh trên Heap rất ít), không gây áp lực cho Garbage Collector (Mượt mượt mượt!).

---

## 4. Hardware Deep Dive lúc Reconnecting & NAT Table (Mạng & CPU)
Khi SignalR nằm trong trạng thái `Reconnecting` và chờ theo vòng lặp `infiniteRetry` (ví dụ mỗi 10 giây):

1. **Zero CPU Overhead**: Hàm `setTimeout(retry, 10000)` không dùng lệnh `while(true)` (Busy-Waiting) của CPU. Mã nguồn gọi hệ thống (Syscall) chuyển giao bộ đếm thời gian (Timer) cho OS Kernel (VD: `epoll` trên Linux hoặc `I/O Completion Ports` trên Windows). CPU hoàn toàn RẢNH TAY (`Idle` hoặc vào trạng thái ngủ `C-States` để tiết kiệm điện). Các thanh ghi (Registers) không lưu giá trị gì của kết nối này cả.
2. **Quá trình vươn tay ra ngoài Internet (The NAT Router)**: Khi 10 giây trôi qua, OS Kernel đánh thức Thread lên (CPU nhảy số), OS bắn một gói tin TCP/IP `SYN` xuống NIC.
   - Gói tin chạy qua Modem/Router của công ty. Router ghi nhận kết nối ra ngoài internet và tạo 1 dòng ghi nhớ trong RAM của nó gọi là **Bảng NAT (Network Address Translation Table)**. 
   - Nếu lúc rớt mạng, Router bị khởi động lại, Bảng NAT cũ bị xóa sạch. Nếu dùng SignalR bản cũ (Long Polling) thì chết chắc, nhưng WebSockets sẽ đơn giản yêu cầu TCP 3-way handshake tóm lấy đường truyền mới tạo một dòng NAT hoàn toàn mới.
3. **Quá trình Handshake & SSL/TLS (CPU Burst)**: Máy chủ phản hồi `SYN-ACK`. Máy khách gửi `ACK`. Tiếp tục là quá trình TLS Handshake mã hóa (Math-heavy). Trong vài mili-giây ngắn ngủi này, đơn vị tính toán Logic (`ALU`) của CPU và các thanh ghi mã hóa hoạt động 100% công suất để bắt tay mã hóa với .NET 8 Server. Sau khi bắt tay xong, Socket được cấp lại Ring-Buffer trên RAM HĐH, Event `socket.onopen` đá ngược lên App -> Kích hoạt `onreconnected` trên vùng **Call Stack** của Code, đổi trạng thái UI xanh lè "Connected". Mọi thứ tĩnh lặng trở lại, CPU lại về 0%.

---

## 5. Trả lời câu hỏi: Vòng lặp `setTimeout` 10s có sinh ra "Callback Hell" hay tràn "Stack Frame" không?

Có một câu hỏi cực kỳ hóc búa từ các chuyên gia: *"Nếu cứ 10 giây thất bại lại gọi tiếp `setTimeout` một lần, nó có bị gọi lồng nhau liên tục tạo thành Callback Hell dẫn tới Memory Leak hoặc Stack Overflow không?"*

**Câu trả lời là hoàn toàn KHÔNG (Zero Memory Leak & O(1) Stack).** Lý do nằm ở bản chất của **Asynchronous Event Loop** (Vòng lặp sự kiện bất đồng bộ) trong JavaScript/Trình duyệt (hoặc `Task` trong .NET):

1. **Pop khỏi Call Stack (Xóa sổ sau khi gọi)**: Khi Code chạy lệnh `setTimeout(retry, 10000)`, Engine V8 đẩy nhiệm vụ đếm ngược này cho **Hệ điều hành / Web APIs** (C-level Syscall). Ngay lập tức, hàm hiện tại kết thúc (hoàn thành) và **BỊ XÓA KHỎI TẦNG CALL STACK (Popped)**. CPU hoàn toàn trống.
2. **Khoảng chờ 10 Giây**: Lúc này Call Stack của SignalR liên quan đến Reconnect là **RỖNG (EMPTY)**. Memory (RAM) không hề ghi nhận một chồng Stack Frame nào đang chờ cả. Pointer của Callback "Nằm im" ở dạng cấu trúc dữ liệu trên Heap.
3. **Thực thi lần tiếp theo**: Khi OS đếm ngược xong 10 giây, OS ném cái Callback vào **Task Queue (Hàng đợi sự kiện)**. Event Loop của JS Engine thấy Call Stack đang Rỗng -> Bốc Callback đưa lên Call Stack chạy. 
4. **Vòng lặp không lồng nhau**: Nếu kết nối TCP tiếp tục thất bại, nó lại gọi tiếp `setTimeout(retry, 10000)`, đẩy cho OS, và lại **POP (Xóa sổ)** thân hàm chính nó ra khỏi Stack lần nữa. 
5. **Đánh giá bản chất**: Mô hình gọi đệ quy bất đồng bộ kiểu này (Asynchronous Recursion) không bao giờ lồng Frame (No Nested Frames). Tại mọi hướng đo lường (Kể cả rớt mạng 1 tuần), số lượng Stack Frame cho xử lý kết nối luôn luôn là `1` hoặc `0`. Việc RAM cạn kiệt (Memory Leak) do đệ quy là chuyện **nghịch lý** và không thể xảy ra.

---

## 6. Lật bài ngửa: Vậy ai / Cái gì trực tiếp "Đếm giờ" trong 10 giây chờ đứt cáp?

Nếu JavaScript (hay C#) đã bị Popped khỏi CPU, Stack trống, vậy CPU hay Heap đếm thời gian? Câu trả lời là CẢ HAI ĐỀU KHÔNG ĐẾM, người đếm là **Chip phần cứng của Mainboard và Hệ Điều Hành (OS Kernel)**.

Cơ chế chạy ngầm (Under the hood):
1. **Trao quyền (Web API / OS)**: Lệnh `setTimeout(10000)` trong JS gọi một hàm C++ dưới Engine (Node/V8/Web APIs). Tại đây, Javascript bàn giao "Con trỏ hàm Callback" cho C++ lưu tạm trên Heap, rồi JS "phủi tay" bỏ đi (Call Stack trống 100%).
2. **Chip đếm giờ phần cứng (HPET)**: C++ uỷ thác việc đếm giờ xuống **Kernel của Hệ Điều Hành** (Windows/Linux/Mac). OS không hề bắt CPU dùng vòng lặp `while(now < target)` để đếm (vì như vậy là 100% CPU). Thay vào đó, OS đăng ký thời hạn 10 giây vào một **Chip Bộ đếm phần cứng (Hardware Timer - VD: HPET hoặc RTC trên Mainboard)**.
3. **CPU Nghỉ ngơi (C-States)**: Trong 10 giây, CPU không hề bỏ ra 1 Hertz nào để nghĩ về cái SignalR đang đứt. Nó RẢNH RỖI.
4. **Hardware Interrupt (Đánh thức)**: Đúng 10.000ms sau, Chip Timer trên Mainboard nháy một xung điện báo hiệu cho CPU (IRQ - Hardware Interrupt).
5. **Event Loop Thức Tỉnh**: Bấy giờ CPU mới ngắt việc đang làm, nhảy vào OS Kernel xử lý ngắt. OS lục trong cấu trúc dữ liệu `Timer Queue` của OS xem Hẹn giờ nào vừa tới hạn -> Nó lôi cái Callback gửi trả ngược lại cho JavaScript Event Queue (Hàng đợi của trình duyệt).
6. **Lên sóng**: Event Loop của JS Engine gắp cái Callback ra rồi quăng lên Call Stack khởi động gọi TCP lên Server.

**Đúc kết cực kỳ ngắn gọn trước sếp:** *"Không có CPU nào đếm giờ cả, mà nó dùng **Hardware Timers Board** gõ chuông đánh thức OS. Trong thời gian chờ 10 giây đứt cáp, CPU và Stack Memory hoàn toàn được Release (giải phóng) hoàn toàn để chạy việc khác"*. Do đó tốn 0% CPU lúc rớt mạng.

## 4. SignalR Bindings: Cách Map C# RPC với Frontend
```typescript
this.hubConnection.on('NewMessageNotification', () => this.newMessageSubject.next());
this.hubConnection.on('UserReadReceipt', (data) => this.userReadReceiptSubject.next(data));
```
**Deep Dive**:
- Khi Server C# gọi `Clients.Group("Vip").SendAsync("NewMessageNotification", jobId)`, SignalR sẽ gói tin này thành chuỗi JSON (hoặc MessagePack cực nhanh nếu bạn cấu hình).
- Gói tin chạy qua TCP Pipeline về máy khách. Thread phụ của SignalR hứng luồng Binary/Text này -> Parse (Giải mã) thành Object.
- Nó so khớp chuỗi `"NewMessageNotification"` với cái Dictionary (Bảng băm) các hàm đã đăng ký `hubConnection.on(...)` do ta cấu hình bên trên.
- Nếu Hash khớp nhau, nó gọi Callback để đẩy logic sang luồng xử lý chính. Toàn bộ quá trình từ Server xuống Client chỉ tốn vài **Microgiây (µs)** độ trễ. Quá trình Parse JSON cực kỳ ít tốn RAM do cơ chế quản lý bằng ArrayBuffer tuần tự.

---

## TỔNG KẾT VỀ LUẬN ĐIỂM BẢO VỆ CHUNG
Khi bị hỏi về hiệu năng: *"Sử dụng cái này trên máy khách yếu, máy in cùi có lag, có ăn RAM/CPU/Network không?"*

**Khẳng định (Thuyết trình):**
1. **Network**: Hầu như bằng 0 (Zero Idle Byte). Tắt hoàn toàn Heartbeat rác. Dây truyền tải chỉ vận động duy nhất lúc máy chủ nôn Job ra. So với phương án chọc Polling API HTTP mỗi 5s truyền thống tốn tài nguyên gấp cả ngàn lần.
2. **CPU**: Hầu như 0%. Nó là kiến trúc hướng sự kiện (Interrupt Driven). Idle Socket không ép vi xử lý làm việc. Wait Task trong retry chạy bằng Timer Kernel.
3. **RAM**: Rất nhỏ gọn. Không nạp file nặng qua WebSockets, SignalR chỉ giao nhiệm vụ truyền đi mỗi cặp Key-Value siêu bé `{ JobId = 123 }`. Còn việc down file PDF trăm megabyte được nhường cho `HttpClient` chạy độc lập, download và xả xuống Disk tự động xong dọn RAM (Garbage Collector).

Đây là cách chuẩn hóa tốt nhất (Best Practice) cho giao thức Real-time của IoT và In ấn phân tán.
