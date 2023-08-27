"use strict";

var connection = new signalR.HubConnectionBuilder().withUrl("https://localhost:5003/chatHub").build();

connection.on("ReceiveMessage", function (user, message) {
    var preMessage = document.getElementById('divload').innerHTML;
    document.getElementById("divload").innerHTML = message + "<br/><br/>" + preMessage;
});

connection.start().then(function () {

}).catch(function (err) {
    return console.error(err.toString());
});


$(document).ready(function () {    
    $("#btnsubmit").on('click', function () {
        console.log('clicked');
        var payload = $('#txtInputText').val();
        $.get("https://localhost:5003/send?payload=" + payload, function (data, status) {
            var preMessage = document.getElementById('divResponse').innerHTML;
            document.getElementById('divResponse').innerHTML = data.message + "<br/><br/>" + preMessage;
        });
    });
});