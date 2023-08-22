"use strict";

var connection = new signalR.HubConnectionBuilder().withUrl("https://localhost:5003/chatHub").build();

connection.on("ReceiveMessage", function (user, message) {    
    document.getElementById("divload").innerHTML = message;
});

connection.start().then(function () {
    
}).catch(function (err) {
    return console.error(err.toString());
});